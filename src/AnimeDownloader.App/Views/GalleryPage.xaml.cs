using AnimeDownloader.App.Controls;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 画廊页：随机 / 分页两种模式展示缩略图网格，支持自动刷新与点击打开全屏查看器。
/// 图源与 NSFW 过滤由主窗口全局工具栏提供（对应参考项目窗口级 source_selector）。
/// </summary>
public sealed partial class GalleryPage : Page, IModePage
{
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

    public GalleryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnPageLoaded();
        Unloaded += (_, _) => OnPageUnloaded();
    }

    /// <summary>页面回到可视树：恢复自动刷新（若已启用）。</summary>
    private void OnPageLoaded()
    {
        if (_owner is not null && AutoReloadSwitch.IsOn && !_isLoading)
        {
            StartAutoReload();
        }
    }

    /// <summary>页面离开可视树：暂停自动刷新，避免后台空转。</summary>
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
        // 无论是否首次进入，只要画廊为空就尝试加载（覆盖首次/失败后切回场景）
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

    // ---------------- 状态恢复 ----------------

    private void RestoreState()
    {
        // 子模式：random / paged（来自持久化设置）；抑制控件事件避免重复加载
        _restoringState = true;
        try
        {
            _pagedMode = _settings.GallerySubmode == "paged";
            ModeToggle.IsChecked = _pagedMode;
            _galleryCount = _settings.GalleryCount > 0 ? _settings.GalleryCount : 12;
            AutoReloadSwitch.IsOn = _settings.AutoReloadEnabled;
        }
        finally
        {
            _restoringState = false;
        }
    }

    private void UpdateModeControls()
    {
        ModeToggle.Content = _pagedMode ? "分页模式" : "随机模式";
        var supportsPaging = _pagedMode && _currentSource.SupportsPaging;
        PageControls.Visibility = supportsPaging ? Visibility.Visible : Visibility.Collapsed;
        PagingHint.Visibility = _pagedMode && !_currentSource.SupportsPaging
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrevPageButton.IsEnabled = _page > 1;
        PageLabel.Text = $"第 {_page} 页";
    }

    private void OnModeToggleChecked(object sender, RoutedEventArgs e)
    {
        if (_restoringState)
        {
            return;
        }

        _pagedMode = true;
        _settings.GallerySubmode = "paged";
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    private void OnModeToggleUnchecked(object sender, RoutedEventArgs e)
    {
        if (_restoringState)
        {
            return;
        }

        _pagedMode = false;
        _settings.GallerySubmode = "random";
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

    /// <summary>请求一次重载：递增代际 token 使在途请求的结果作废，避免竞态。</summary>
    private Task RequestReload()
    {
        _reloadToken++;
        return ReloadAsync();
    }

    private void OnAutoReloadToggled(object sender, RoutedEventArgs e)
    {
        if (_restoringState)
        {
            return;
        }

        _settings.AutoReloadEnabled = AutoReloadSwitch.IsOn;
        _settingsStore.Save(_settings);
        if (AutoReloadSwitch.IsOn)
        {
            StartAutoReload();
        }
        else
        {
            StopAutoReload();
        }
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

            // 期间用户已切换源/模式/页码：丢弃过期结果，交给挂起重载。
            if (token != _reloadToken)
            {
                return;
            }

            _items = items;
            PopulateGrid();
            if (items.Count == 0)
            {
                StatusText.Text = "没有找到图片。可尝试更换图源、切换 NSFW 模式或调整图源标签。";
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
            else if (AutoReloadSwitch.IsOn && IsLoaded)
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
            var card = new ImageCard
            {
                Item = item,
                Downloader = downloader,
            };
            ThumbGrid.Items.Add(card);
        }
    }

    private void OnThumbClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ImageCard card || card.Item is null || _owner is null)
        {
            return;
        }

        var index = _items.ToList().IndexOf(card.Item);
        var viewer = new Controls.GalleryViewerWindow(_owner, _items, index);
        viewer.Activate();
    }
}
