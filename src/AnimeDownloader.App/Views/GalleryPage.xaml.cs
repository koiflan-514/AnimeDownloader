using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Storage.Pickers;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 画廊页：随机 / 分页缩略图网格，支持标签快速搜索、自动刷新、错峰入场动画、
/// 悬浮快捷保存与整页批量下载（带进度与取消）。
/// </summary>
#pragma warning disable CA1001 // _batchCts is disposed in the batch finally block.
public sealed partial class GalleryPage : Page, IModePage
{
#pragma warning restore CA1001
    private const double ItemSpacing = 8;
    private const double MinItemSize = 120;
    private const double MaxItemSize = 220;

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

    public GalleryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnPageLoaded();
        Unloaded += (_, _) => OnPageUnloaded();
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
        PrevPageButton.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        NextPageButton.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        PrevPageButton.IsEnabled = _page > 1;
        PageLabel.Text = $"第 {_page} 页";
        GalleryHint.Text = _pagedMode && _currentSource.SupportsPaging
            ? $"{_currentSource.DisplayName} · 浏览第 {_page} 页精选作品"
            : $"{_currentSource.DisplayName} · 每次刷新都会带来新的发现";
        UpdateTagControls();
    }

    private void UpdateTagControls()
    {
        var supportsTags = _currentSource.SupportsTags;
        TagSearchHost.Visibility = supportsTags ? Visibility.Visible : Visibility.Collapsed;
        if (!supportsTags)
        {
            TagChip.Visibility = Visibility.Collapsed;
            return;
        }

        var tags = _currentSource.Tags ?? string.Empty;
        TagBox.Text = tags;
        var hasTags = !string.IsNullOrWhiteSpace(tags);
        TagChip.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
        TagChipText.Text = hasTags ? $"标签：{tags}" : string.Empty;
    }

    // ---------------- 标签搜索 ----------------

    private void OnTagApply(object sender, RoutedEventArgs e) => ApplyTags(TagBox.Text);

    private void OnTagBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ApplyTags(TagBox.Text);
        }
    }

    private void OnClearTag(object sender, RoutedEventArgs e) => ApplyTags(string.Empty);

    /// <summary>应用标签到当前图源并保存、刷新；空串表示清除标签。</summary>
    private void ApplyTags(string tags)
    {
        if (_owner is null || !_currentSource.SupportsTags)
        {
            return;
        }

        tags = tags.Trim();
        _currentSource.Tags = tags;
        _settings.SourceTags[_currentSource.Id] = tags;
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    // ---------------- 模式切换与分页 ----------------

    private void OnModeToggleClick(object sender, RoutedEventArgs e)
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

    private void OnPrevPage(object sender, RoutedEventArgs e) => ChangePage(-1);

    private void OnNextPage(object sender, RoutedEventArgs e) => ChangePage(1);

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

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or AutoSuggestBox)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Right when _pagedMode && _currentSource.SupportsPaging:
                e.Handled = true;
                ChangePage(1);
                break;
            case VirtualKey.Left when _pagedMode && _currentSource.SupportsPaging:
                e.Handled = true;
                ChangePage(-1);
                break;
            case VirtualKey.Space:
                e.Handled = true;
                _ = RequestReload();
                break;
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = RequestReload();

    private void OnWelcomeBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_settings.WelcomeHintDismissed)
        {
            return;
        }

        _settings.WelcomeHintDismissed = true;
        _settingsStore.Save(_settings);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => _owner?.NavigateToSettings();

    private void OnAutoReloadClick(object sender, RoutedEventArgs e)
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
        _autoReloadTimer = new DispatcherTimer();
        var seconds = _settings.AutoReloadIntervalSeconds > 0
            ? _settings.AutoReloadIntervalSeconds
            : 30;
        _autoReloadTimer.Interval = TimeSpan.FromSeconds(seconds);
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
        RetryButton.Visibility = Visibility.Collapsed;
        CancelBatchButton.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
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
                ShowEmptyState("这里空空如也", message);
                RetryButton.Visibility = Visibility.Visible;
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
            RetryButton.Visibility = Visibility.Visible;
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
        EmptyActionButton.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    private void SetStatusDot(bool busy = false, bool error = false)
    {
        var key = error
            ? "AppStatusDotErrorStyle"
            : busy ? "AppStatusDotBusyStyle" : "AppStatusDotOkStyle";
        StatusDot.Style = (Style)Application.Current.Resources[key];
    }

    private void PopulateGrid()
    {
        ThumbGrid.Items.Clear();
        var downloader = _owner?.Downloader;
        for (var i = 0; i < _items.Count; i++)
        {
            var card = new Controls.ImageCard
            {
                Downloader = downloader,
                ThumbnailProxyTemplate = _settings.ThumbnailProxyTemplate,
                Item = _items[i],
                EntranceDelay = TimeSpan.FromMilliseconds(i * 28),
            };
            if (_itemSize > 0)
            {
                card.SetCardSize(_itemSize);
            }

            card.QuickSaveRequested += OnCardQuickSave;
            card.OpenRequested += (s, _) => OpenViewer((Controls.ImageCard)s!);
            ThumbGrid.Items.Add(card);
        }
    }

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGridLayout();
    }

    /// <summary>按窗口宽度重算方形卡片尺寸（2-8 列）。</summary>
    private void UpdateGridLayout()
    {
        if (ThumbGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        var width = ThumbGrid.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var columns = Math.Clamp((int)Math.Round(width / 200), 2, 8);
        var itemSize = Math.Clamp(
            (width - ItemSpacing * (columns - 1)) / columns,
            MinItemSize,
            MaxItemSize);
        panel.ItemWidth = itemSize;
        panel.ItemHeight = itemSize;
        _itemSize = itemSize;
        // 卡片显式定宽高：避免 GridViewItem 按图片自然尺寸测量导致首卡容器异常变高
        foreach (var item in ThumbGrid.Items)
        {
            if (item is Controls.ImageCard card)
            {
                card.SetCardSize(itemSize);
            }
        }
    }

    /// <summary>单击卡片 = 直接打开大图查看器（300ms 防抖避免双击重复开窗）。</summary>
    private void OnThumbClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Controls.ImageCard card)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastOpenUtc < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        _lastOpenUtc = now;
        OpenViewer(card);
    }

    /// <summary>打开大图查看器（单击 / 悬浮「打开」按钮 / 回车触发）。</summary>
    private void OpenViewer(Controls.ImageCard card)
    {
        var owner = _owner;
        if (card.Item is null || owner is null)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], card.Item))
            {
                index = i;
                break;
            }
        }

        owner.SetHeaderThumbnail(card.Item);
        var viewer = new Controls.GalleryViewerWindow(owner, _items, index);
        viewer.Activate();
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

    private async void OnDownloadAll(object sender, RoutedEventArgs e)
    {
        if (_owner is null || _items.Count == 0)
        {
            return;
        }

        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var directory = folder.Path;
        _settings.DownloadDirectory = directory;
        _settingsStore.Save(_settings);
        await RunBatchAsync(directory);
    }

    private async Task RunBatchAsync(string directory)
    {
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        DownloadAllButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        CancelBatchButton.Visibility = Visibility.Visible;
        BatchProgress.Visibility = Visibility.Visible;
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
            CancelBatchButton.Visibility = Visibility.Collapsed;
            BatchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelBatch(object sender, RoutedEventArgs e)
    {
        _batchCts?.Cancel();
    }
}