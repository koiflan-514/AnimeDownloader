using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 画廊页：随机 / 分页缩略图网格，支持标签快速搜索、自动刷新、错峰入场动画、
/// 悬浮快捷保存与整页批量下载（带进度与取消）。
/// </summary>
#pragma warning disable CA1001 // _batchCts is disposed in the batch finally block.
public sealed partial class GalleryPage : UserControl, IModePage
{
#pragma warning restore CA1001
    // 接触印相节奏：更少的列、更大的相纸、更紧的栏距。
    // 目标是让每一格读起来像一张照片，而不是一个「缩略图」。
    //
    // 关于间隙（CardGutter）：它必须是**瓦片之内**的差额，而不是宽度算式里的预留项。
    // 旧写法把列宽算成 (width - 10*(n-1))/n 却让卡片铺满瓦片，于是间隙从来没出现过 ——
    // 卡片彼此贴死，而算式预留出来的那 10*(n-1) 变成右侧空掉的一条。
    // 现在的规则：瓦片 = 行宽 ÷ 列数（精确铺满，换行面板不会多出一格去换行），
    // 卡片 = 瓦片 - CardGutter（居中），相邻两张之间自然是 CardGutter，
    // 最外两侧各让出 CardGutter/2 —— 左右对称，不是缺陷。
    private const double CardGutter = 10;
    private const double IdealCardSize = 238;
    private const int MaxColumns = 8;

    /// <summary>同一次「打开大图」手势的防抖窗口：按下即开窗，双击会来两次。</summary>
    private static readonly TimeSpan OpenDebounce = TimeSpan.FromMilliseconds(300);

    private MainWindow? _owner;
    private IReadOnlyList<IImageSource> _sources = Array.Empty<IImageSource>();
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;

    private IImageSource _currentSource = null!;
    private NsfwMode _nsfwMode = NsfwMode.BlockNsfw;
    private bool _pagedMode;
    private bool _restoringState;
    private int _page = 1;
    private bool _isLoading;
    private bool _pendingReload;
    private int _reloadToken;
    private DispatcherTimer? _autoReloadTimer;
    private int _galleryCount;
    private IReadOnlyList<ImageItem> _items = Array.Empty<ImageItem>();
    private CancellationTokenSource? _batchCts;
    private double _itemSize;
    private DateTime _lastOpenUtc = DateTime.MinValue;

    private CancellationTokenSource? _suggestCts;
    private bool _suppressSuggestEvents;

    public GalleryPage()
    {
        InitializeComponent();
        // Popup 的定位目标只能在代码里给：XAML 里写 {Binding #TagSearchHost} 属于
        // 元素名绑定，而本项目开着编译期绑定（AvaloniaUseCompiledBindingsByDefault），
        // 名称绑定在 DataTemplate 之外虽然可用，但显式赋值零歧义、也不依赖名称作用域。
        TagSuggestPopup.PlacementTarget = TagSearchHost;
        TagSuggestPopup.Placement = PlacementMode.BottomEdgeAlignedLeft;

        Loaded += (_, _) => OnPageLoaded();
        Unloaded += (_, _) => OnPageUnloaded();
        KeyDown += OnPageKeyDown;
    }

    private void OnPageLoaded()
    {
        if (_owner is not null && AutoReloadToggle.IsChecked == true && !_isLoading)
        {
            StartAutoReload();
        }
    }

    private void OnPageUnloaded()
    {
        StopAutoReload();
    }

    // ---------------- IModePage ----------------

    public void Attach(MainWindow owner)
    {
        _owner = owner;
        _sources = owner.Sources;
        _settingsStore = owner.SettingsStore;
        _settings = owner.Settings;
        _currentSource = owner.CurrentSource;
        _nsfwMode = owner.CurrentNsfwMode;

        RestoreState();
        UpdateModeControls();
        if (_items.Count == 0)
        {
            _ = RequestReload();
        }
    }

    public void OnSourceChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _currentSource = _owner.CurrentSource;
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    public void OnNsfwChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _nsfwMode = _owner.CurrentNsfwMode;
        _ = RequestReload();
    }

    public void Reload() => _ = RequestReload();

    // ---------------- State ----------------

    private void RestoreState()
    {
        _restoringState = true;
        try
        {
            _pagedMode = _settings.GallerySubmode == "paged";
            ModeToggle.IsChecked = _pagedMode;
            _galleryCount = Math.Clamp(_settings.GalleryCount > 0 ? _settings.GalleryCount : 12, 1, 48);
            AutoReloadToggle.IsChecked = _settings.AutoReloadEnabled;
            WelcomeBar.IsOpen = !_settings.WelcomeHintDismissed;
        }
        finally
        {
            _restoringState = false;
        }
    }

    private void UpdateModeControls()
    {
        ModeToggle.Content = _pagedMode ? "分页模式" : "随机模式";
        ModeToggle.IsChecked = _pagedMode;
        var supportsPaging = _pagedMode && _currentSource.SupportsPaging;
        PrevPageButton.IsVisible = supportsPaging;
        NextPageButton.IsVisible = supportsPaging;
        PageLabel.IsVisible = supportsPaging;
        PrevPageButton.IsEnabled = _page > 1;
        PageLabel.Text = $"第 {_page} 页";
        GalleryHint.Text = _pagedMode && _currentSource.SupportsPaging
            ? $"{_currentSource.DisplayName} · 第 {_page} 页"
            : $"{_currentSource.DisplayName} · 随机模式";
        UpdateTagControls();
    }

    private void UpdateTagControls()
    {
        var supportsTags = _currentSource.SupportsTags;
        TagSearchHost.IsVisible = supportsTags;
        if (!supportsTags)
        {
            TagChip.IsVisible = false;
            return;
        }

        var tags = _currentSource.Tags ?? string.Empty;
        _suppressSuggestEvents = true;
        try
        {
            TagBox.Text = tags;
        }
        finally
        {
            _suppressSuggestEvents = false;
        }

        var hasTags = !string.IsNullOrWhiteSpace(tags);
        TagChip.IsVisible = hasTags;
        TagChipText.Text = hasTags ? $"标签：{tags}" : string.Empty;
    }

    // ---------------- 标签搜索 ----------------

    private void OnTagApply(object? sender, RoutedEventArgs e) => ApplyTags(TagBox.Text ?? string.Empty);

    private void OnTagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyTags(TagBox.Text ?? string.Empty);
        }
    }

    private void OnClearTag(object? sender, RoutedEventArgs e) => ApplyTags(string.Empty);

    // ---------------- 标签联想（复选框智能提示） ----------------

    /// <summary>输入变化：防抖后按最后一个词查询联想与相关标签。</summary>
    private void OnTagBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_owner is null || _suppressSuggestEvents || _currentSource is not ITagSuggester suggester)
        {
            return;
        }

        if (!suggester.SupportsTagSuggestions)
        {
            return;
        }

        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _ = UpdateSuggestionsAsync(suggester, TagBox.Text ?? string.Empty, _suggestCts.Token);
    }

    private async Task UpdateSuggestionsAsync(ITagSuggester suggester, string text, CancellationToken ct)
    {
        var word = LastWord(text);
        if (word.Length == 0)
        {
            TagSuggestPopup.IsOpen = false;
            return;
        }

        try
        {
            var suggestions = await suggester.SuggestTagsAsync(word, 12, ct).ConfigureAwait(true);
            IReadOnlyList<TagSuggestion> related = Array.Empty<TagSuggestion>();
            try
            {
                related = await suggester.GetRelatedTagsAsync(word, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 相关标签查询失败不影响联想列表。
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (suggestions.Count == 0 && related.Count == 0)
            {
                TagSuggestPopup.IsOpen = false;
                return;
            }

            BuildSuggestList(suggestions, related, word);
            TagSuggestPopup.IsOpen = true;
            // 弹层打开会抢占键盘焦点：立即把焦点还给输入框，保证连续输入不中断
            TagBox.Focus();
            TagBox.CaretIndex = (TagBox.Text ?? string.Empty).Length;
        }
        catch (OperationCanceledException)
        {
            // 防抖取消：忽略
        }
        catch (Exception)
        {
            TagSuggestPopup.IsOpen = false;
        }
    }

    private void BuildSuggestList(IReadOnlyList<TagSuggestion> suggestions, IReadOnlyList<TagSuggestion> related, string word)
    {
        SuggestList.Children.Clear();
        RelatedList.Children.Clear();
        var existing = CurrentTagWords(TagBox.Text ?? string.Empty);

        SuggestHeaderText.Text = suggestions.Count > 0
            ? "标签联想（勾选加入搜索）"
            : "没有匹配的联想标签";
        foreach (var suggestion in suggestions)
        {
            SuggestList.Children.Add(BuildSuggestionCheckBox(suggestion, existing, word));
        }

        RelatedHeaderText.IsVisible = related.Count > 0;
        foreach (var suggestion in related)
        {
            RelatedList.Children.Add(BuildSuggestionCheckBox(suggestion, existing, word));
        }
    }

    private CheckBox BuildSuggestionCheckBox(TagSuggestion suggestion, HashSet<string> existing, string word)
    {
        var checkBox = new CheckBox
        {
            IsChecked = existing.Contains(suggestion.Name),
            MinHeight = 32,
            Padding = new Thickness(6, 2, 6, 2),
            // 点击候选项时把键盘焦点留在输入框，避免输入被打断。
            // WinUI 版是 AllowFocusOnInteraction=false，Avalonia 没有这个属性，
            // 等价手段是让勾选框根本不接受焦点（键盘仍可通过 Tab 之前的位置操作）。
            Focusable = false,
        };

        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
        };
        if (suggestion.Category is { } category)
        {
            panel.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Fill = new SolidColorBrush(CategoryColor(category)),
            });
        }

        var primary = suggestion.ChineseName ?? suggestion.Name;
        panel.Children.Add(new TextBlock
        {
            Text = primary,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (suggestion.ChineseName is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = suggestion.Name,
                Foreground = Res("AppTextSecondaryBrush"),
                FontSize = 11.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (suggestion.PostCount is { } count)
        {
            panel.Children.Add(new TextBlock
            {
                Text = FormatTagCount(count),
                Foreground = Res("AppTextTertiaryBrush"),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        checkBox.Content = panel;
        var tooltipLines = new List<string> { suggestion.Name };
        if (suggestion.ChineseName is not null)
        {
            tooltipLines.Insert(0, $"中文：{suggestion.ChineseName}");
        }

        if (suggestion.Category is { } cat)
        {
            tooltipLines.Add($"类别：{CategoryDisplayName(cat)}");
        }

        if (suggestion.PostCount is { } posts)
        {
            tooltipLines.Add($"帖子数：{posts}");
        }

        ToolTip.SetTip(checkBox, string.Join('\n', tooltipLines));
        var tagName = suggestion.Name;
        checkBox.IsCheckedChanged += (_, _) =>
        {
            if (checkBox.IsChecked == true)
            {
                ToggleTagWord(tagName, add: true);
            }
            else
            {
                ToggleTagWord(tagName, add: false);
            }
        };
        return checkBox;
    }

    /// <summary>把勾选的标签加入输入框（替换未完成的最后一个词），取消勾选则移除。</summary>
    private void ToggleTagWord(string tag, bool add)
    {
        var text = TagBox.Text ?? string.Empty;
        var trimmed = text.TrimEnd();
        var endsWithSpace = text.Length > 0 && text.EndsWith(' ');
        var words = new List<string>(
            trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!endsWithSpace && words.Count > 0)
        {
            // 末尾是尚未应用的输入片段：不计入已完成标签，将被勾选的标签替换
            words.RemoveAt(words.Count - 1);
        }

        if (add)
        {
            if (!words.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                words.Add(tag);
            }

            SetTagBoxText(words.Count > 0 ? string.Join(' ', words) + " " : string.Empty);
        }
        else
        {
            SetTagBoxText(string.Join(
                ' ',
                words.Where(w => !w.Equals(tag, StringComparison.OrdinalIgnoreCase))));
        }
    }

    private void SetTagBoxText(string text)
    {
        _suppressSuggestEvents = true;
        try
        {
            TagBox.Text = text;
            TagBox.CaretIndex = text.Length;
        }
        finally
        {
            _suppressSuggestEvents = false;
        }
    }

    private static HashSet<string> CurrentTagWords(string text) => new(
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    private static string LastWord(string text)
    {
        var trimmed = text.TrimEnd();
        var idx = trimmed.LastIndexOf(' ');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static Color CategoryColor(TagCategory category) => category switch
    {
        TagCategory.Artist => Color.FromArgb(255, 0xE8, 0xA2, 0x3D),
        TagCategory.Character => Color.FromArgb(255, 0x35, 0xC0, 0x75),
        TagCategory.Copyright => Color.FromArgb(255, 0x9B, 0x59, 0xD0),
        TagCategory.Meta => Color.FromArgb(255, 0xE5, 0x48, 0x4D),
        TagCategory.Circle => Color.FromArgb(255, 0x4C, 0xA6, 0xC9),
        _ => Color.FromArgb(255, 0x80, 0x80, 0x80),
    };

    private static string CategoryDisplayName(TagCategory category) => category switch
    {
        TagCategory.Artist => "画师",
        TagCategory.Character => "角色",
        TagCategory.Copyright => "作品",
        TagCategory.Meta => "元数据",
        TagCategory.Circle => "社团",
        _ => "通用",
    };

    private static string FormatTagCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>按 key 取一支画笔（正文里的次级文本色等）。取不到时返回 null，交给默认前景。</summary>
    /// <remarks>
    /// Avalonia 12 没有 WinUI 的 <c>TryFindResource(key, out value)</c>；对应物是
    /// <see cref="StyledElement.TryGetResource(object, ThemeVariant, out object?)"/>，
    /// 它要求显式给出主题变体 —— 传 <c>ActualThemeVariant</c> 才等价于「按当前主题找」。
    /// </remarks>
    private IBrush? Res(string key) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    /// <summary>应用标签到当前图源并保存、刷新；空串表示清除标签。</summary>
    private void ApplyTags(string tags)
    {
        if (_owner is null || !_currentSource.SupportsTags)
        {
            return;
        }

        TagSuggestPopup.IsOpen = false;
        tags = tags.Trim();
        _currentSource.Tags = tags;
        _settings.SourceTags[_currentSource.Id] = tags;
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    // ---------------- 模式切换与分页 ----------------

    private void OnModeToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_restoringState)
        {
            return;
        }

        _pagedMode = ModeToggle.IsChecked == true;
        _settings.GallerySubmode = _pagedMode ? "paged" : "random";
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    private void OnPrevPage(object? sender, RoutedEventArgs e) => ChangePage(-1);

    private void OnNextPage(object? sender, RoutedEventArgs e) => ChangePage(1);

    private void ChangePage(int delta)
    {
        if (_page + delta < 1)
        {
            return;
        }

        _page += delta;
        UpdateModeControls();
        _ = RequestReload();
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox or AutoCompleteBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when _pagedMode && _currentSource.SupportsPaging:
                e.Handled = true;
                ChangePage(1);
                break;
            case Key.Left when _pagedMode && _currentSource.SupportsPaging:
                e.Handled = true;
                ChangePage(-1);
                break;
            case Key.Space:
                e.Handled = true;
                _ = RequestReload();
                break;
        }
    }

    private void OnRetry(object? sender, RoutedEventArgs e) => _ = RequestReload();

    private void OnWelcomeBarClosed(object? sender, EventArgs args)
    {
        if (_settings.WelcomeHintDismissed)
        {
            return;
        }

        _settings.WelcomeHintDismissed = true;
        _settingsStore.Save(_settings);
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => _owner?.NavigateToSettings();

    private void OnAutoReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_restoringState)
        {
            return;
        }

        _settings.AutoReloadEnabled = AutoReloadToggle.IsChecked == true;
        _settingsStore.Save(_settings);
        if (_settings.AutoReloadEnabled)
        {
            StartAutoReload();
        }
        else
        {
            StopAutoReload();
        }
    }

    private Task RequestReload()
    {
        _reloadToken++;
        return ReloadAsync();
    }

    private void StartAutoReload()
    {
        StopAutoReload();
        var seconds = _settings.AutoReloadIntervalSeconds > 0
            ? _settings.AutoReloadIntervalSeconds
            : 30;
        _autoReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _autoReloadTimer.Tick += (_, _) => _ = RequestReload();
        _autoReloadTimer.Start();
    }

    private void StopAutoReload()
    {
        _autoReloadTimer?.Stop();
        _autoReloadTimer = null;
    }

    // ---------------- 加载 ----------------

    private async Task ReloadAsync()
    {
        if (_isLoading)
        {
            _pendingReload = true;
            return;
        }

        _isLoading = true;
        var token = ++_reloadToken;
        StopAutoReload();
        LoadingRing.IsActive = true;
        RetryButton.IsVisible = false;
        CancelBatchButton.IsVisible = false;
        EmptyStatePanel.IsVisible = false;
        SetStatusDot(busy: true);
        StatusText.Text = "加载中…";
        _owner?.SetGlobalStatus("加载中…", busy: true);
        try
        {
            IReadOnlyList<ImageItem> items;
            if (_pagedMode && _currentSource.SupportsPaging)
            {
                items = await _currentSource.GetImagesPageAsync(_nsfwMode, _page, _galleryCount);
            }
            else
            {
                items = await _currentSource.GetImagesAsync(_nsfwMode, _galleryCount);
            }

            if (token != _reloadToken)
            {
                return;
            }

            _items = items;
            PopulateGrid();
            if (items.Count == 0)
            {
                var message = string.IsNullOrEmpty(_currentSource.LastError)
                    ? "没有找到图片。可尝试更换图源、切换 NSFW 模式或调整标签。"
                    : $"加载失败：{_currentSource.LastError}";
                ShowEmptyState("没有找到图片", message);
                RetryButton.IsVisible = true;
                SetStatusDot(error: true);
                StatusText.Text = message;
                _owner?.SetGlobalStatus("没有找到图片", error: true);
            }
            else
            {
                SetStatusDot();
                StatusText.Text = $"{_currentSource.DisplayName} · 共 {items.Count} 张图片";
                _owner?.SetGlobalStatus(StatusText.Text);
            }
        }
        catch (Exception ex)
        {
            if (token != _reloadToken)
            {
                return;
            }

            ShowEmptyState("加载失败", ex.Message);
            RetryButton.IsVisible = true;
            SetStatusDot(error: true);
            StatusText.Text = $"加载失败：{ex.Message}";
            _owner?.SetGlobalStatus("加载失败", error: true);
        }
        finally
        {
            LoadingRing.IsActive = false;
            _isLoading = false;
            if (_pendingReload)
            {
                _pendingReload = false;
                _ = RequestReload();
            }
            else if (AutoReloadToggle.IsChecked == true && IsLoaded)
            {
                StartAutoReload();
            }
        }
    }

    private void ShowEmptyState(string title, string message)
    {
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;
        EmptyActionButton.IsVisible = true;
        EmptyStatePanel.IsVisible = true;
    }

    private void SetStatusDot(bool busy = false, bool error = false)
    {
        // 三档互斥：故障优先。Avalonia 用伪类，不能像 WinUI 那样把 Style 当值赋给控件。
        StatusDot.Classes.Set("error", error);
        StatusDot.Classes.Set("busy", busy && !error);
    }

    private void PopulateGrid()
    {
        // 换批前先把旧卡片回收掉：每张卡都持着一枚缩略图下载的 CTS 和一枚微光动画的 CTS，
        // 光 Children.Clear() 只是「摘下来」，在跑的下载与动画仍然活着。
        // （这一条是 CA1001 逼出来的真问题，不是为过分析器而补的形式。）
        foreach (var child in ThumbPanel.Children)
        {
            if (child is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        ThumbPanel.Children.Clear();
        var downloader = _owner?.Downloader;
        for (var i = 0; i < _items.Count; i++)
        {
            // 注意顺序：Item 的赋值会**同步**触发 LoadThumbnail 直到第一个 await，
            // 因此 ThumbnailProxyTemplate 必须先于 Item 设好，否则首屏缩略图会绕过代理模板。
            var card = new Controls.ImageCard
            {
                Downloader = downloader,
                ThumbnailProxyTemplate = _settings.ThumbnailProxyTemplate,
                EntranceDelay = TimeSpan.FromMilliseconds(i * 28),
                Item = _items[i],
            };
            if (_itemSize > 0)
            {
                card.SetCardSize(_itemSize);
            }

            card.QuickSaveRequested += OnCardQuickSave;
            card.OpenRequested += (_, item) => OpenViewer(item);
            ThumbPanel.Children.Add(card);
        }
    }

    private void OnGridSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateGridLayout();

    /// <summary>按视口宽度重算方形相纸尺寸（2-8 列）。</summary>
    private void UpdateGridLayout()
    {
        // TileWrapPanel 拿到的就是 ScrollViewer 的视口宽度（水平滚动已禁用），
        // 所以这里用面板自身的宽度，而不是某个外层容器的宽度。
        var width = ThumbPanel.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        // 瓦片精确铺满整行：n 个瓦片不超过可用宽度，换行面板才不会把最后一张挤到下一行。
        var columns = Math.Clamp(
            (int)Math.Round(width / (IdealCardSize + CardGutter)),
            2,
            MaxColumns);
        // 向下取整（再留半像素余量），这一步不能省。面板实际拿到的是 ScrollViewer 的视口宽度，
        // 它带小数；瓦片若取除法原值，n 个瓦片就会刚好等于可用宽度而攒出一次多余的换行 ——
        // 症状是最右一列整列消失（每行都只排满 n-1 张），很容易误判成「列数算错」。
        var tile = Math.Floor((width - 0.5) / columns);
        var itemSize = tile - CardGutter;

        ThumbPanel.ItemWidth = tile;
        ThumbPanel.ItemHeight = tile;
        _itemSize = itemSize;
        // 卡片显式定宽高：避免图像按自身比例参与测量导致容器高度漂移
        foreach (var child in ThumbPanel.Children)
        {
            if (child is Controls.ImageCard card)
            {
                card.SetCardSize(itemSize);
            }
        }
    }

    /// <summary>打开大图查看器（单击卡片 / 悬浮「打开」按钮 / 回车触发）。</summary>
    private void OpenViewer(ImageItem item)
    {
        var owner = _owner;
        if (owner is null)
        {
            return;
        }

        // 单击即开窗，而双击会来两次 —— 300ms 的窗口把第二次吃掉，
        // 避免「双击看大图」变成两扇一模一样的窗口。
        var now = DateTime.UtcNow;
        if (now - _lastOpenUtc < OpenDebounce)
        {
            return;
        }

        _lastOpenUtc = now;

        var index = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
            {
                index = i;
                break;
            }
        }

        owner.SetHeaderThumbnail(item);
        var viewer = new Controls.GalleryViewerWindow(owner, _items, index);
        viewer.Show();
    }

    // ---------------- 快捷保存 ----------------

    private async void OnCardQuickSave(object? sender, ImageItem item)
    {
        var owner = _owner;
        if (owner is null || item is null)
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(_settings.DownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : _settings.DownloadDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, item.SuggestFileName());
            await owner.Downloader.DownloadToFileAsync(item.Url, target);
            StatusText.Text = $"已保存：{Path.GetFileName(target)}";
            SetStatusDot();
            _owner?.SetGlobalStatus(StatusText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
            SetStatusDot(error: true);
            _owner?.SetGlobalStatus("保存失败", error: true);
        }
    }

    // ---------------- 批量下载 ----------------

    private async void OnDownloadAll(object? sender, RoutedEventArgs e)
    {
        if (_owner is null || _items.Count == 0)
        {
            return;
        }

        // WinUI 用 FolderPicker + PickerLocationId.Downloads；Avalonia 走 TopLevel.StorageProvider。
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            startLocation = await topLevel.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
        }
        catch (Exception)
        {
            // 拿不到「下载」文件夹就退回系统默认位置，不影响功能。
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择批量下载目录",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0)
        {
            return;
        }

        var directory = folders[0].Path.LocalPath;
        _settings.DownloadDirectory = directory;
        _settingsStore.Save(_settings);
        await RunBatchAsync(directory);
    }

    private async Task RunBatchAsync(string directory)
    {
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        DownloadAllButton.IsEnabled = false;
        RetryButton.IsVisible = false;
        CancelBatchButton.IsVisible = true;
        BatchProgress.IsVisible = true;
        BatchProgress.Value = 0;
        StatusText.Text = "准备下载…";
        SetStatusDot(busy: true);
        _owner?.SetGlobalStatus("批量下载中…", busy: true);

        var progress = new Progress<BatchDownloadProgress>(p =>
        {
            BatchProgress.Value = p.Total > 0 ? (double)p.Completed / p.Total : 0;
            StatusText.Text = $"下载中 {p.Completed}/{p.Total}";
        });

        try
        {
            var result = await _owner!.Batch.DownloadAllAsync(_items, directory, progress, token);
            SetStatusDot();
            StatusText.Text = result.Failed == 0
                ? $"已保存 {result.Succeeded} 张到 {directory}"
                : $"完成：成功 {result.Succeeded}，失败 {result.Failed}";
            _owner?.SetGlobalStatus(StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消批量下载";
            _owner?.SetGlobalStatus("已取消批量下载");
        }
        catch (Exception ex)
        {
            SetStatusDot(error: true);
            StatusText.Text = $"批量下载失败：{ex.Message}";
            _owner?.SetGlobalStatus("批量下载失败", error: true);
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            DownloadAllButton.IsEnabled = true;
            CancelBatchButton.IsVisible = false;
            BatchProgress.IsVisible = false;
        }
    }

    private void OnCancelBatch(object? sender, RoutedEventArgs e)
    {
        _batchCts?.Cancel();
    }
}
