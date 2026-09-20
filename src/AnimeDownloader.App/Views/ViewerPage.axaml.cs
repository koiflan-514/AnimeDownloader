using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 查看器页：沉浸式大图浏览，浮动工具栏支持缩放 / 适应 / 保存 / 全屏，
/// 键盘快捷键（空格 / 方向键换图，Ctrl+S 保存，F11 全屏，Esc 退出），
/// 左下角显示作者与分辨率信息胶囊。
///
/// 实现 <see cref="IDisposable"/> 是因为它持有完整解码的原图（<see cref="Bitmap"/>）——
/// 见文件末尾 <c>ReleaseImage</c> 的说明。
/// </summary>
public sealed partial class ViewerPage : UserControl, IModePage, IImmersivePage, IDisposable
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4;

    private MainWindow? _owner;
    private SettingsStore _settingsStore = null!;
    private ImageDownloader _downloader = null!;
    private IImageSource _currentSource = null!;
    private NsfwMode _nsfwMode = NsfwMode.BlockNsfw;
    private ImageItem? _current;
    private byte[]? _content;
    private Bitmap? _image;
    private int _loadToken;
    private bool _isSaving;
    private Stretch _stretch = Stretch.Uniform;
    private double _zoom = 1;

    public ViewerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
        KeyDown += OnPageKeyDown;

        // 缩放要吃掉 Ctrl+滚轮，必须在**隧道**阶段拦：等 ScrollViewer 自己收到时
        // 它已经开始滚动了（WinUI 那边是 handledEventsToo 的等价意图）。
        ViewScroller.AddHandler(
            PointerWheelChangedEvent,
            OnScrollerWheel,
            RoutingStrategies.Tunnel);

        // 视口尺寸一变就重算「适应屏幕」的落点 —— 这相当于 WinUI 里
        // Image.Stretch=Uniform 自动跟随容器重排的那部分行为。
        ViewScroller.SizeChanged += (_, _) => ApplyImageLayout();
    }

    // ---------------- IModePage ----------------

    public void Attach(MainWindow owner)
    {
        _owner = owner;
        _settingsStore = owner.SettingsStore;
        _downloader = owner.Downloader;
        _currentSource = owner.CurrentSource;
        _nsfwMode = owner.CurrentNsfwMode;
        if (_current is null)
        {
            _ = LoadRandomAsync();
        }
    }

    public void OnSourceChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _currentSource = _owner.CurrentSource;
        _ = LoadRandomAsync();
    }

    public void OnNsfwChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _nsfwMode = _owner.CurrentNsfwMode;
        _ = LoadRandomAsync();
    }

    public void Reload() => _ = LoadRandomAsync();

    private async Task LoadRandomAsync()
    {
        var token = ++_loadToken;
        LoadingRing.IsActive = true;
        ErrorPanel.IsVisible = false;
        ReleaseImage();
        _stretch = Stretch.Uniform;
        _zoom = 1;
        ViewScroller.Offset = default;
        ZoomLabel.Text = "100%";
        SourceLinkButton.IsVisible = false;
        SaucenaoButton.IsVisible = false;
        IqdbButton.IsVisible = false;
        CopyLinkButton.IsVisible = false;
        TagsHost.IsVisible = false;
        InfoChips.IsVisible = false;
        StatusText.Text = "加载中…";
        SetStatusDot(busy: true);
        _owner?.SetGlobalStatus("加载中…", busy: true);
        _owner?.SetHeaderThumbnail(null);
        try
        {
            var item = await _currentSource.GetRandomImageAsync(_nsfwMode);
            if (token != _loadToken)
            {
                return;
            }

            if (item is null)
            {
                ErrorText.Text = string.IsNullOrEmpty(_currentSource.LastError)
                    ? "没有获取到图片。可尝试更换图源或 NSFW 模式。"
                    : $"加载失败：{_currentSource.LastError}";
                ErrorPanel.IsVisible = true;
                StatusText.Text = "加载失败";
                SetStatusDot(error: true);
                _owner?.SetGlobalStatus("加载失败", error: true);
                _owner?.SetHeaderThumbnail(null);
                return;
            }

            _current = item;
            _content = null;
            _content = await _downloader.DownloadAsync(item.Url);
            if (token != _loadToken)
            {
                return;
            }

            // WinUI 是 BitmapImage.SetSource(随机访问流)；Avalonia 对应 Bitmap(Stream)。
            // 这里刻意不做有界解码 —— 灯箱要的就是原图（与 WinUI 版一致，
            // 也因此它是一次贵操作：本机实测 40 秒量级，取决于图源与网络）。
            using var stream = new MemoryStream(_content);
            _image = new Bitmap(stream);
            ViewImage.Source = _image;
            ApplyImageLayout();
            // 首帧里 Viewport 可能还是 0（图片刚挂上、布局还没算），等一次布局再补算。
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (token == _loadToken)
                    {
                        ApplyImageLayout();
                    }
                },
                DispatcherPriority.Loaded);

            MetaText.Text = string.Join(" · ", new[]
            {
                _currentSource.DisplayName,
                item.Artist ?? string.Empty,
                item.SourceLink ?? string.Empty,
            }.Where(s => s.Length > 0));
            SourceLinkButton.IsVisible = !string.IsNullOrEmpty(item.SourceLink);

            // 增值功能：以图搜图 / 复制直链（原图 URL 有效时可用）
            CopyLinkButton.IsVisible = true;
            SaucenaoButton.IsVisible = true;
            IqdbButton.IsVisible = true;
            PopulateTags(item);

            ArtistText.Text = item.Artist ?? string.Empty;
            ArtistChip.IsVisible = !string.IsNullOrEmpty(item.Artist);
            if (item.Dimensions is { } dims)
            {
                ResText.Text = $"{dims.Width} × {dims.Height}";
                ResChip.IsVisible = true;
            }
            else
            {
                ResChip.IsVisible = false;
            }

            InfoChips.IsVisible = ArtistChip.IsVisible || ResChip.IsVisible;

            StatusText.Text = "就绪";
            SetStatusDot();
            _owner?.SetGlobalStatus("就绪");
            _owner?.SetHeaderThumbnail(item);
        }
        catch (Exception ex)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = $"加载失败：{ex.Message}";
            ErrorPanel.IsVisible = true;
            StatusText.Text = "加载失败";
            SetStatusDot(error: true);
            _owner?.SetGlobalStatus("加载失败", error: true);
            _owner?.SetHeaderThumbnail(null);
        }
        finally
        {
            if (token == _loadToken)
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    private void SetStatusDot(bool busy = false, bool error = false)
    {
        StatusDot.Classes.Set("error", error);
        StatusDot.Classes.Set("busy", busy && !error);
    }

    // ---------------- 标签区 ----------------

    /// <summary>填充当前图片的全部标签 chip；点击 chip 跳转到该标签的画廊视图。</summary>
    private void PopulateTags(ImageItem item)
    {
        TagsList.Children.Clear();
        var tags = item.TagList;
        TagsHost.IsVisible = tags.Count > 0;
        if (tags.Count == 0)
        {
            return;
        }

        foreach (var tag in tags)
        {
            var hasChinese = TagLocalization.TryGet(tag.Name, out var localized);
            var chip = new Button
            {
                Theme = Controls.AppTheme.Lookup("AppTagChipButton"),
                // WrapPanel 没有间距属性，chip 自己的 Margin 就是行/列间距。
                Margin = new Thickness(0, 0, 6, 6),
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var dotColor = tag.Category switch
            {
                TagCategory.Artist => Color.FromArgb(255, 0xE8, 0xA2, 0x3D),
                TagCategory.Character => Color.FromArgb(255, 0x35, 0xC0, 0x75),
                TagCategory.Copyright => Color.FromArgb(255, 0x9B, 0x59, 0xD0),
                TagCategory.Meta => Color.FromArgb(255, 0xE5, 0x48, 0x4D),
                TagCategory.Circle => Color.FromArgb(255, 0x4C, 0xA6, 0xC9),
                _ => (Color?)null,
            };
            if (dotColor is { } color)
            {
                panel.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var parts = new List<string>();
            if (hasChinese)
            {
                parts.Add(localized.ChineseName);
            }

            parts.Add(tag.Name);
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(' ', parts),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            chip.Content = panel;

            var tooltipLines = new List<string> { tag.Name };
            if (hasChinese)
            {
                tooltipLines.Insert(0, $"中文：{localized.ChineseName}");
            }

            if (tag.Category is { } category)
            {
                tooltipLines.Add($"类别：{CategoryDisplayName(category)}");
            }

            ToolTip.SetTip(chip, string.Join('\n', tooltipLines));
            var tagName = tag.Name;
            chip.Click += (_, _) => _owner?.NavigateToTagView(tagName);
            TagsList.Children.Add(chip);
        }
    }

    private static string CategoryDisplayName(TagCategory category) => category switch
    {
        TagCategory.Artist => "画师",
        TagCategory.Character => "角色",
        TagCategory.Copyright => "作品",
        TagCategory.Meta => "元数据",
        TagCategory.Circle => "社团",
        _ => "通用",
    };

    // ---------------- 增值功能：复制直链 / 以图搜图 ----------------

    private async void OnCopyImageLink(object? sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                StatusText.Text = "复制失败：剪贴板不可用";
                return;
            }

            // Avalonia 12 的 IClipboard 只有 SetDataAsync(IAsyncDataTransfer)，
            // 纯文本这一档由 ClipboardExtensions.SetTextAsync 包好（using Avalonia.Input.Platform）。
            await clipboard.SetTextAsync(_current.Url);
            StatusText.Text = "已复制图片直链";
            SetStatusDot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OnOpenSaucenao(object? sender, RoutedEventArgs e) =>
        OpenReverseSearch("https://saucenao.com/search.php?url=");

    private void OnOpenIqdb(object? sender, RoutedEventArgs e) =>
        OpenReverseSearch("https://iqdb.org/?url=");

    private async void OpenReverseSearch(string baseUrl)
    {
        if (_current is null)
        {
            return;
        }

        var url = baseUrl + Uri.EscapeDataString(_current.Url);
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                StatusText.Text = "打开反搜失败：系统启动器不可用";
                SetStatusDot(error: true);
                return;
            }

            _ = await launcher.LaunchUriAsync(new Uri(url));
            StatusText.Text = "已在浏览器打开反搜";
            SetStatusDot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开反搜失败：{ex.Message}";
            SetStatusDot(error: true);
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnRetry(object? sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Space:
            case Key.Right:
                e.Handled = true;
                _ = LoadRandomAsync();
                break;
            // F11 / 全屏下的 Esc 由 MainWindow 统一处理（窗口级 KeyDown + 外壳收层）：
            // 页面自己也去动窗口状态的话，会出现「窗口进了全屏、外壳却没收起」的半吊子状态。
            case Key.S when ctrl:
                e.Handled = true;
                OnSave(sender, new RoutedEventArgs());
                break;
            case Key.D0 when ctrl:
                e.Handled = true;
                OnFitToScreen(sender, new RoutedEventArgs());
                break;
            case Key.Add or Key.OemPlus when ctrl:
                e.Handled = true;
                ZoomBy(1.25);
                break;
            case Key.Subtract or Key.OemMinus when ctrl:
                e.Handled = true;
                ZoomBy(0.8);
                break;
        }
    }

    private void OnScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        ZoomBy(e.Delta.Y > 0 ? 1.1 : 0.9);
    }

    /// <summary>适应屏幕：完整显示整张图片（等比缩放）。</summary>
    private void OnFitToScreen(object? sender, RoutedEventArgs e)
    {
        _stretch = Stretch.Uniform;
        _zoom = 1;
        ViewScroller.Offset = default;
        ApplyImageLayout();
        ZoomLabel.Text = "100%";
    }

    /// <summary>实际大小：按图片原始像素 1:1 显示。</summary>
    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        _stretch = Stretch.None;
        _zoom = 1;
        ViewScroller.Offset = default;
        ApplyImageLayout();
        ZoomLabel.Text = "100%";
    }

    /// <summary>拉伸填充：铺满整个可视区域（可能变形）。</summary>
    private void OnStretchFill(object? sender, RoutedEventArgs e)
    {
        _stretch = Stretch.Fill;
        _zoom = 1;
        ViewScroller.Offset = default;
        ApplyImageLayout();
        ZoomLabel.Text = "100%";
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ZoomBy(1.25);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ZoomBy(0.8);

    private void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        ApplyImageLayout();
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
    }

    /// <summary>
    /// 把「当前缩放模式 + 当前倍率」落成图片的显式显示尺寸。
    ///
    /// 为什么不是直接给 ScrollViewer 一个 zoom：Avalonia 的 ScrollViewer 没有 ZoomMode
    /// （那是 WinUI 的 ChangeView(null,null,zoom) 才有的能力）。显式算尺寸的语义完全等价，
    /// 而且滚动条由框架按最终尺寸自然给出 —— 「实际大小」时超出视口就有滚动条，
    /// 「适应屏幕」时刚好铺满就没有，与 WinUI 版逐条对得上。
    /// </summary>
    private void ApplyImageLayout()
    {
        if (_image is null)
        {
            return;
        }

        var pixel = _image.PixelSize;
        if (pixel.Width <= 0 || pixel.Height <= 0)
        {
            return;
        }

        var viewport = ViewScroller.Viewport;
        if (viewport.Width <= 1 || viewport.Height <= 1)
        {
            // 布局未就绪：保持住上一次的尺寸，等 SizeChanged 或图片加载后的补算。
            return;
        }

        double width;
        double height;
        switch (_stretch)
        {
            case Stretch.None:
                width = pixel.Width;
                height = pixel.Height;
                break;
            case Stretch.Fill:
                width = viewport.Width;
                height = viewport.Height;
                break;
            default:
                var scale = Math.Min(viewport.Width / pixel.Width, viewport.Height / pixel.Height);
                width = pixel.Width * scale;
                height = pixel.Height * scale;
                break;
        }

        // 图片自己总是 Fill（忠实铺满给定盒子）；等比由上面算出的盒子承担，
        // 因此只有「拉伸填充」模式才会变形 —— 与 Stretch 的语义一一对应。
        ViewImage.Stretch = Stretch.Fill;
        ViewImage.Width = Math.Max(1, width * _zoom);
        ViewImage.Height = Math.Max(1, height * _zoom);
    }

    private void OnViewAreaDoubleTapped(object? sender, TappedEventArgs e) => ToggleFullscreen();

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _owner?.ToggleFullscreen();
    }

    // ---------------- IImmersivePage ----------------

    /// <summary>
    /// 由 MainWindow 在整窗全屏切换时调用。窗口状态不在这里动 —— 那是外壳的职责，
    /// 页面只负责收起 / 恢复自己这一层（工具栏、标签、状态条、圆角与内边距）。
    /// </summary>
    public void SetImmersive(bool immersive)
    {
        if (immersive)
        {
            OnFitToScreen(this, new RoutedEventArgs());
            HideChrome();
        }
        else
        {
            ShowChrome();
        }
    }

    private void HideChrome()
    {
        ToolbarHeader.IsVisible = false;
        TagsHost.IsVisible = false;
        FloatingToolbar.IsVisible = false;
        InfoChips.IsVisible = false;
        StatusBar.IsVisible = false;
        ViewArea.CornerRadius = new CornerRadius(0);
        Root.Padding = new Thickness(0);
    }

    private void ShowChrome()
    {
        ToolbarHeader.IsVisible = true;
        FloatingToolbar.IsVisible = true;
        StatusBar.IsVisible = true;
        InfoChips.IsVisible = ArtistChip.IsVisible || ResChip.IsVisible;
        // 标签区只在图片带标签时恢复显示
        TagsHost.IsVisible = _current is { } current && current.TagList.Count > 0;
        ViewArea.CornerRadius = CornerRadiusToken();
        Root.Padding = PagePaddingToken();
    }

    /// <summary>
    /// 页级内边距与台面圆角。
    ///
    /// 初始值刻意保持 <c>0</c>（与 WinUI 版一致）：原版只在**退出全屏**那条路径里
    /// 把这两项恢复成设计令牌，首屏从未设过。也就是说「第一次进出全屏之后，
    /// 查看器内容会获得一层 28px 的页边距」是原版的实际行为 ——
    /// 这里原样保留，不做「顺手修一下」；若哪天要修，改这两处的初始值即可。
    ///
    /// 取令牌用 <c>TryGetResource</c> 而非 WinUI 的 <c>TryFindResource</c>：
    /// Avalonia 12 后者不存在，前者要求显式传主题变体。
    /// </summary>
    private Thickness PagePaddingToken() =>
        TryGetResource("PagePadding", ActualThemeVariant, out var value) && value is Thickness thickness
            ? thickness
            : new Thickness(28, 22, 28, 0);

    private CornerRadius CornerRadiusToken() =>
        TryGetResource("PanelCornerRadius", ActualThemeVariant, out var value) && value is CornerRadius radius
            ? radius
            : new CornerRadius(14);

    private async void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (_current?.SourceLink is null)
        {
            return;
        }

        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                return;
            }

            _ = await launcher.LaunchUriAsync(new Uri(_current.SourceLink));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开来源失败：{ex.Message}";
            SetStatusDot(error: true);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_current is null || _owner is null || _isSaving)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        _isSaving = true;
        SaveButton.IsEnabled = false;
        try
        {
            if (_content is null)
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var percent = p.Percent is { } value ? (int)(value * 100) : 0;
                    StatusText.Text = percent > 0 ? $"保存中 {percent}%" : "保存中…";
                    _owner?.SetGlobalStatus("保存中…", busy: true);
                });
                _content = await _downloader.DownloadAsync(_current.Url, progress);
            }

            var extension = _current.Extension ?? "png";
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图片",
                SuggestedFileName = _current.SuggestFileName(),
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("图片") { Patterns = new[] { $"*.{extension}" } },
                },
            });
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(_content);
            StatusText.Text = "已保存";
            SetStatusDot();
            _owner?.SetGlobalStatus("已保存");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
            SetStatusDot(error: true);
            _owner?.SetGlobalStatus("保存失败", error: true);
        }
        finally
        {
            SaveButton.IsEnabled = true;
            _isSaving = false;
        }
    }

    // ==================== 位图回收 ====================

    /// <summary>
    /// 释放当前大图。灯箱刻意不做有界解码（要的就是原图），一张位图可能几十 MB，
    /// 所以「换图」和「关窗」都必须真的放掉，而不是只把引用置空。
    /// 窗口关闭时由 <see cref="MainWindow"/> 统一回收缓存页，这里由它调到。
    /// </summary>
    public void Dispose()
    {
        ReleaseImage();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 把当前位图从 Image 上摘下来并释放。
    ///
    /// 释放刻意延到下一轮消息循环（后台优先级）：位图在渲染线程那头还挂着上一帧的引用，
    /// 当场 Dispose 有概率让渲染线程踩到已释放的位图。先把 Source 摘空（画面立刻变空），
    /// 再让渲染跑完这一帧后才真正释放。
    /// </summary>
    private void ReleaseImage()
    {
        var previous = _image;
        _image = null;
        ViewImage.Source = null;
        ViewImage.Width = double.NaN;
        ViewImage.Height = double.NaN;

        if (previous is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }
}
