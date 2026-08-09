using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AnimeDownloader.App.Views;

/// <summary>
/// Gallery page: random / paged thumbnail grid with auto reload, responsive item sizing and
/// whole-page batch download with progress and cancellation.
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
        }
        finally
        {
            _restoringState = false;
        }
    }

    private void UpdateModeControls()
    {
        ModeToggle.Label = _pagedMode ? "分页模式" : "随机模式";
        ModeToggle.IsChecked = _pagedMode;
        var supportsPaging = _pagedMode && _currentSource.SupportsPaging;
        PrevPageButton.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        NextPageButton.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        PrevPageButton.IsEnabled = _page > 1;
        PageLabel.Text = $"第 {_page} 页";
    }

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

    private void OnRetry(object sender, RoutedEventArgs e) => _ = RequestReload();

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
        StatusText.Text = "加载中…";
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
                StatusText.Text = string.IsNullOrEmpty(_currentSource.LastError)
                    ? "没有找到图片。可尝试更换图源、切换 NSFW 模式或调整图源标签。"
                    : $"加载失败：{_currentSource.LastError}";
                RetryButton.Visibility = Visibility.Visible;
            }
            else
            {
                StatusText.Text = $"{_currentSource.DisplayName} · 共 {items.Count} 张图片";
            }
        }
        catch (Exception ex)
        {
            if (token != _reloadToken)
            {
                return;
            }

            StatusText.Text = $"加载失败：{ex.Message}";
            RetryButton.Visibility = Visibility.Visible;
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

    private void PopulateGrid()
    {
        ThumbGrid.Items.Clear();
        var downloader = _owner?.Downloader;
        foreach (var item in _items)
        {
            var card = new Controls.ImageCard
            {
                Downloader = downloader,
                ThumbnailProxyTemplate = _settings.ThumbnailProxyTemplate,
                Item = item,
            };
            ThumbGrid.Items.Add(card);
        }
    }

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGridLayout();
    }

    /// <summary>Recomputes square item size so the grid fills the window with 2-8 columns.</summary>
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

        var columns = Math.Clamp((int)Math.Round(width / 190), 2, 8);
        var itemSize = Math.Clamp(
            (width - ItemSpacing * (columns - 1)) / columns,
            MinItemSize,
            MaxItemSize);
        panel.ItemWidth = itemSize;
        panel.ItemHeight = itemSize;
    }

    private void OnThumbClick(object sender, ItemClickEventArgs e)
    {
        var owner = _owner;
        if (e.ClickedItem is not Controls.ImageCard card || card.Item is null || owner is null)
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

    // ---------------- Batch download ----------------

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
        CancelBatchButton.Visibility = Visibility.Visible;
        BatchProgress.Visibility = Visibility.Visible;
        BatchProgress.Value = 0;
        StatusText.Text = "准备下载…";

        var progress = new Progress<BatchDownloadProgress>(p =>
        {
            BatchProgress.Value = p.Total > 0 ? (double)p.Completed / p.Total : 0;
            StatusText.Text = $"下载中 {p.Completed}/{p.Total}";
        });

        try
        {
            var result = await _owner!.Batch.DownloadAllAsync(_items, directory, progress, token);
            StatusText.Text = result.Failed == 0
                ? $"已保存 {result.Succeeded} 张到 {directory}"
                : $"完成：成功 {result.Succeeded}，失败 {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消批量下载";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"批量下载失败：{ex.Message}";
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
