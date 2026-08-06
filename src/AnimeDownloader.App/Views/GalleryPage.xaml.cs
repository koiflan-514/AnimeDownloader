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
/// 对应参考项目的 gallery 视图。
/// </summary>
public sealed partial class GalleryPage : Page
{
    private MainWindow? _owner;
    private IReadOnlyList<IImageSource> _sources = Array.Empty<IImageSource>();
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;

    private IImageSource _currentSource = null!;
    private NsfwMode _nsfwMode = NsfwMode.BlockNsfw;
    private bool _pagedMode;
    private int _page = 1;
    private bool _isLoading;
    private bool _pendingReload;
    private int _reloadToken;
    private DispatcherTimer? _autoReloadTimer;
    private int _galleryCount;
    private IReadOnlyList<ImageItem> _items = Array.Empty<ImageItem>();
    private readonly List<ImageCard> _cards = new();

    public GalleryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainWindow owner)
        {
            return;
        }

        _owner = owner;
        _sources = owner.Sources;
        _settingsStore = owner.SettingsStore;
        _settings = owner.SettingsStore.Load();

        PopulateSourceCombo();
        RestoreState();
        if (_items.Count == 0)
        {
            _ = RequestReload();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopAutoReload();
    }

    private void PopulateSourceCombo()
    {
        SourceCombo.Items.Clear();
        foreach (var source in _sources)
        {
            SourceCombo.Items.Add(source);
        }
    }

    private void RestoreState()
    {
        // 恢复保存的设置
        var savedSource = _settings.SelectedSource;
        for (var i = 0; i < SourceCombo.Items.Count; i++)
        {
            if (SourceCombo.Items[i] is IImageSource s && s.Id == savedSource)
            {
                SourceCombo.SelectedIndex = i;
                break;
            }
        }

        if (SourceCombo.SelectedIndex < 0 && SourceCombo.Items.Count > 0)
        {
            SourceCombo.SelectedIndex = 0;
        }

        _currentSource = (IImageSource)SourceCombo.SelectedItem;

        // NSFW 模式
        _nsfwMode = _settings.NsfwMode.ToCore();
        for (var i = 0; i < NsfwCombo.Items.Count; i++)
        {
            if (NsfwCombo.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == _nsfwMode.ToString())
            {
                NsfwCombo.SelectedIndex = i;
                break;
            }
        }

        // 子模式：random / paged
        _pagedMode = _settings.GallerySubmode == "paged";
        ModeToggle.IsChecked = _pagedMode;
        UpdateModeControls();

        _galleryCount = _settings.GalleryCount > 0 ? _settings.GalleryCount : 12;

        AutoReloadSwitch.IsOn = _settings.AutoReloadEnabled;
        if (_settings.AutoReloadEnabled)
        {
            StartAutoReload();
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

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not IImageSource source)
        {
            return;
        }

        _currentSource = source;
        _settings.SelectedSource = source.Id;
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    private void OnNsfwChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NsfwCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<NsfwMode>(item.Tag?.ToString(), out var mode))
        {
            return;
        }

        _nsfwMode = mode;
        _settings.NsfwMode = mode.ToStorage();
        _settingsStore.Save(_settings);
        _ = RequestReload();
    }

    private void OnModeToggleChecked(object sender, RoutedEventArgs e)
    {
        _pagedMode = true;
        _settings.GallerySubmode = "paged";
        _settingsStore.Save(_settings);
        _page = 1;
        UpdateModeControls();
        _ = RequestReload();
    }

    private void OnModeToggleUnchecked(object sender, RoutedEventArgs e)
    {
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

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RequestReload();

    /// <summary>请求一次重载：递增代际 token 使在途请求的结果作废，避免竞态。</summary>
    private Task RequestReload()
    {
        _reloadToken++;
        return ReloadAsync();
    }

    private void OnAutoReloadToggled(object sender, RoutedEventArgs e)
    {
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
            StatusText.Text = items.Count == 0
                ? "没有找到图片。请尝试更换标签或 NSFW 模式。"
                : $"共 {items.Count} 张图片";
        }
        catch (Exception ex)
        {
            if (token != _reloadToken)
            {
                return;
            }

            StatusText.Text = $"加载失败：{ex.Message}";
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
            else if (AutoReloadSwitch.IsOn)
            {
                StartAutoReload();
            }
        }
    }

    private void PopulateGrid()
    {
        ThumbGrid.Items.Clear();
        _cards.Clear();
        foreach (var item in _items)
        {
            var card = new ImageCard { Item = item };
            _cards.Add(card);
            ThumbGrid.Items.Add(item);
        }
    }

    private void OnThumbClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ImageItem item || _owner is null)
        {
            return;
        }

        var index = _items.ToList().IndexOf(item);
        var viewer = new Controls.GalleryViewerWindow(_owner, _items, index);
        viewer.Activate();
    }
}
