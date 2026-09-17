using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 查看器页：沉浸式大图浏览，浮动工具栏支持缩放 / 适应 / 保存 / 全屏，
/// 键盘快捷键（空格 / 方向键换图，Ctrl+S 保存，F11 全屏，Esc 退出），
/// 左下角显示作者与分辨率信息胶囊。
/// </summary>
public sealed partial class ViewerPage : Page, IModePage, IImmersivePage
{
    private MainWindow? _owner;
    private SettingsStore _settingsStore = null!;
    private ImageDownloader _downloader = null!;
    private IImageSource _currentSource = null!;
    private NsfwMode _nsfwMode = NsfwMode.BlockNsfw;
    private ImageItem? _current;
    private byte[]? _content;
    private int _loadToken;
    private bool _isSaving;
    private Stretch _stretch = Stretch.Uniform;
    private float _zoom = 1f;

    private const float MinZoom = 0.25f;
    private const float MaxZoom = 4f;

    public ViewerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        ViewScroller.AddHandler(
            PointerWheelChangedEvent,
            new PointerEventHandler(OnScrollerWheel),
            handledEventsToo: true);
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
        ErrorPanel.Visibility = Visibility.Collapsed;
        ViewImage.Source = null;
        ViewScroller.ChangeView(null, null, 1f);
        ViewImage.Stretch = Stretch.Uniform;
        _stretch = Stretch.Uniform;
        _zoom = 1f;
        ZoomLabel.Text = "100%";
        SourceLinkButton.Visibility = Visibility.Collapsed;
        SaucenaoButton.Visibility = Visibility.Collapsed;
        IqdbButton.Visibility = Visibility.Collapsed;
        CopyLinkButton.Visibility = Visibility.Collapsed;
        TagsHost.Visibility = Visibility.Collapsed;
        InfoChips.Visibility = Visibility.Collapsed;
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
                ErrorPanel.Visibility = Visibility.Visible;
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

            var bitmap = new BitmapImage();
            bitmap.SetSource(new MemoryStream(_content).AsRandomAccessStream());
            ViewImage.Source = bitmap;
            MetaText.Text = string.Join(" · ", new[]
            {
                _currentSource.DisplayName,
                item.Artist ?? string.Empty,
                item.SourceLink ?? string.Empty,
            }.Where(s => s.Length > 0));
            SourceLinkButton.Visibility = string.IsNullOrEmpty(item.SourceLink)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 增值功能：以图搜图 / 复制直链（原图 URL 有效时可用）
            CopyLinkButton.Visibility = Visibility.Visible;
            SaucenaoButton.Visibility = Visibility.Visible;
            IqdbButton.Visibility = Visibility.Visible;
            PopulateTags(item);

            ArtistText.Text = item.Artist ?? string.Empty;
            ArtistChip.Visibility = string.IsNullOrEmpty(item.Artist)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (item.Dimensions is { } dims)
            {
                ResText.Text = $"{dims.Width} × {dims.Height}";
                ResChip.Visibility = Visibility.Visible;
            }
            else
            {
                ResChip.Visibility = Visibility.Collapsed;
            }

            InfoChips.Visibility = (ArtistChip.Visibility == Visibility.Visible ||
                                    ResChip.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;

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
            ErrorPanel.Visibility = Visibility.Visible;
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
        var key = error
            ? "AppStatusDotErrorStyle"
            : busy ? "AppStatusDotBusyStyle" : "AppStatusDotOkStyle";
        StatusDot.Style = (Style)Application.Current.Resources[key];
    }

    // ---------------- 标签区 ----------------

    /// <summary>填充当前图片的全部标签 chip；点击 chip 跳转到该标签的画廊视图。</summary>
    private void PopulateTags(ImageItem item)
    {
        TagsList.Items.Clear();
        var tags = item.TagList;
        TagsHost.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tags.Count == 0)
        {
            return;
        }

        foreach (var tag in tags)
        {
            var hasChinese = TagLocalization.TryGet(tag.Name, out var localized);
            var chip = new Button { Style = (Style)Application.Current.Resources["AppTagChipButtonStyle"] };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var dotColor = tag.Category switch
            {
                TagCategory.Artist => Windows.UI.Color.FromArgb(255, 0xE8, 0xA2, 0x3D),
                TagCategory.Character => Windows.UI.Color.FromArgb(255, 0x35, 0xC0, 0x75),
                TagCategory.Copyright => Windows.UI.Color.FromArgb(255, 0x9B, 0x59, 0xD0),
                TagCategory.Meta => Windows.UI.Color.FromArgb(255, 0xE5, 0x48, 0x4D),
                TagCategory.Circle => Windows.UI.Color.FromArgb(255, 0x4C, 0xA6, 0xC9),
                _ => (Windows.UI.Color?)null,
            };
            if (dotColor is { } color)
            {
                panel.Children.Add(new Ellipse
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
            panel.Children.Add(new TextBlock { Text = string.Join(' ', parts), TextTrimming = TextTrimming.CharacterEllipsis });
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

            ToolTipService.SetToolTip(chip, string.Join('\n', tooltipLines));
            var tagName = tag.Name;
            chip.Click += (_, _) => _owner?.NavigateToTagView(tagName);
            TagsList.Items.Add(chip);
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

    private void OnCopyImageLink(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetText(_current.Url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            StatusText.Text = "已复制图片直链";
            SetStatusDot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OnOpenSaucenao(object sender, RoutedEventArgs e) =>
        OpenReverseSearch("https://saucenao.com/search.php?url=");

    private void OnOpenIqdb(object sender, RoutedEventArgs e) =>
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
            _ = await Launcher.LaunchUriAsync(new Uri(url));
            StatusText.Text = "已在浏览器打开反搜";
            SetStatusDot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开反搜失败：{ex.Message}";
            SetStatusDot(error: true);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
            case VirtualKey.Right:
                e.Handled = true;
                _ = LoadRandomAsync();
                break;
            // F11 / 全屏下的 Esc 由 MainWindow 统一处理（窗口级加速器 + 外壳 KeyDown）：
            // 页面自己也动 AppWindow 的话，会出现"窗口进了全屏、外壳却没收起"的半吊子状态。
            case VirtualKey.S when IsCtrlPressed():
                e.Handled = true;
                OnSave(sender, new RoutedEventArgs());
                break;
            case VirtualKey.Number0 when IsCtrlPressed():
                e.Handled = true;
                OnFitToScreen(sender, new RoutedEventArgs());
                break;
            case VirtualKey.Add when IsCtrlPressed():
                e.Handled = true;
                ZoomBy(1.25f);
                break;
            case VirtualKey.Subtract when IsCtrlPressed():
                e.Handled = true;
                ZoomBy(0.8f);
                break;
        }
    }

    private void OnScrollerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCtrlPressed())
        {
            return;
        }

        var delta = e.GetCurrentPoint(ViewScroller).Properties.MouseWheelDelta;
        e.Handled = true;
        ZoomBy(delta > 0 ? 1.1f : 0.9f);
    }

    /// <summary>适应屏幕：完整显示整张图片（等比缩放）。</summary>
    private void OnFitToScreen(object sender, RoutedEventArgs e)
    {
        _stretch = Stretch.Uniform;
        _zoom = 1f;
        ViewImage.Stretch = _stretch;
        ViewScroller.ChangeView(0, 0, 1f);
        ZoomLabel.Text = "100%";
    }

    /// <summary>实际大小：按图片原始像素 1:1 显示。</summary>
    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        _stretch = Stretch.None;
        _zoom = 1f;
        ViewImage.Stretch = _stretch;
        ViewScroller.ChangeView(0, 0, 1f);
        ZoomLabel.Text = "100%";
    }

    /// <summary>拉伸填充：铺满整个可视区域（可能变形）。</summary>
    private void OnStretchFill(object sender, RoutedEventArgs e)
    {
        _stretch = Stretch.Fill;
        _zoom = 1f;
        ViewImage.Stretch = _stretch;
        ViewScroller.ChangeView(0, 0, 1f);
        ZoomLabel.Text = "100%";
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.25f);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(0.8f);

    private void ZoomBy(float factor)
    {
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        ViewScroller.ChangeView(null, null, _zoom);
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private static bool IsCtrlPressed() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnViewAreaDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        _owner?.ToggleFullscreen();
    }

    // ---------------- IImmersivePage ----------------

    /// <summary>
    /// 由 MainWindow 在整窗全屏切换时调用。窗口呈现器不在这里动 —— 那是外壳的职责，
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
        ToolbarHeader.Visibility = Visibility.Collapsed;
        TagsHost.Visibility = Visibility.Collapsed;
        FloatingToolbar.Visibility = Visibility.Collapsed;
        InfoChips.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        ViewArea.CornerRadius = new CornerRadius(0);
        Root.Padding = new Thickness(0);
    }

    private void ShowChrome()
    {
        ToolbarHeader.Visibility = Visibility.Visible;
        FloatingToolbar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        InfoChips.Visibility = (ArtistChip.Visibility == Visibility.Visible ||
                                ResChip.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 标签区只在图片带标签时恢复显示
        TagsHost.Visibility = _current is { } current && current.TagList.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ViewArea.CornerRadius = new CornerRadius(16);
        Root.Padding = (Thickness)Application.Current.Resources["PagePadding"];
    }

    private async void OnOpenSource(object sender, RoutedEventArgs e)
    {
        if (_current?.SourceLink is null)
        {
            return;
        }

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_current.SourceLink));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开来源失败：{ex.Message}";
            SetStatusDot(error: true);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null || _owner is null || _isSaving)
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

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var ext = _current.Extension ?? "png";
            picker.FileTypeChoices.Add("图片", new List<string> { $".{ext}" });
            picker.SuggestedFileName = _current.SuggestFileName();

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await Windows.Storage.FileIO.WriteBytesAsync(file, _content);
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
}
