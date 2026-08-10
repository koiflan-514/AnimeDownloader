using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimeDownloader.App;

/// <summary>
/// Main window: owns app-wide shared state (settings, sources, HttpClient, selected source and
/// NSFW mode) and switches between the gallery / viewer / settings pages. The global toolbar
/// (source, NSFW filter, refresh, status) lives in the left navigation pane; pages react to
/// changes through <see cref="IModePage"/>.
/// </summary>
#pragma warning disable CA1001 // Owned for the application lifetime and disposed on rebuild.
public sealed partial class MainWindow : Window
{
#pragma warning restore CA1001
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<IImageSource> _sources = null!;
    private ImageDownloader _downloader = null!;
    private BatchDownloader _batchDownloader = null!;

    private readonly Dictionary<string, Page> _pageCache = new(StringComparer.Ordinal);
    private Page? _currentPage;

    private IImageSource _currentSource = null!;
    private NsfwMode _currentNsfwMode = NsfwMode.BlockNsfw;
    private bool _syncingToolbar;
    private int _headerThumbToken;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = App.SettingsStore;
        _settings = App.Settings;
        RebuildHttp();
        Title = "AnimeDownloader";
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
        SyncToolbarFromSettings();
        ApplyPaneLayout(compact: false);
        SetLogoSource();
        Win11Chrome.SetIcon(this);
        Win11Chrome.Apply(this, Root);
        Root.Loaded += (_, _) => Win11Chrome.Apply(this, Root);
    }

    private void RebuildHttp()
    {
        var settings = _settings;
        var factory = new HttpClientFactory(settings);
        var http = factory.CreateClient();
        var sources = SourceRegistry.CreateAll(http);
        SourceRegistry.ApplySettings(sources, settings);

        _http?.Dispose();
        _http = http;
        _sources = sources;
        _downloader?.Dispose();
        var cacheDir = Path.Combine(_settingsStore.ConfigDirectory, "cache", "thumbnails");
        _downloader = new ImageDownloader(http, new ImageDownloaderOptions
        {
            MaxConcurrentDownloads = Math.Max(1, settings.MaxConcurrentDownloads),
            EnableThumbnailCache = settings.EnableThumbnailCache,
            CacheDirectory = settings.EnableThumbnailCache ? cacheDir : null,
        });
        _batchDownloader = new BatchDownloader(_downloader);
        _currentSource = FindSource(settings.SelectedSource);
    }

    private void SyncToolbarFromSettings()
    {
        if (_syncingToolbar)
        {
            return;
        }

        _syncingToolbar = true;
        try
        {
            SourceCombo.Items.Clear();
            foreach (var source in _sources)
            {
                SourceCombo.Items.Add(source);
            }

            for (var i = 0; i < SourceCombo.Items.Count; i++)
            {
                if (SourceCombo.Items[i] is IImageSource s && ReferenceEquals(s, _currentSource))
                {
                    SourceCombo.SelectedIndex = i;
                    break;
                }
            }

            _currentNsfwMode = _settings.NsfwMode.ToCore();
            for (var i = 0; i < NsfwCombo.Items.Count; i++)
            {
                if (NsfwCombo.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == _currentNsfwMode.ToString())
                {
                    NsfwCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    private IImageSource FindSource(string? id)
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            if (_sources[i].Id == id)
            {
                return _sources[i];
            }
        }

        return _sources.Count > 0
            ? _sources[0]
            : throw new InvalidOperationException("没有可用图源");
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        RootNav.SelectedItem = GalleryNavItem;
    }

    /// <summary>Expanded pane shows full controls and the status bar in the footer.</summary>
    private void OnPaneOpening(NavigationView sender, object args)
    {
        ApplyPaneLayout(compact: false);
    }

    /// <summary>
    /// Compact pane switches the controls to icon-only form: centered labels, arrow-only
    /// combos, icon-only buttons and the app logo centered in the footer.
    /// </summary>
    private void OnPaneClosing(NavigationView sender, object args)
    {
        ApplyPaneLayout(compact: true);
    }

    private void ApplyPaneLayout(bool compact)
    {
        // 收起态隐藏图源/NSFW/刷新/清理控件区，只保留导航图标与底部 logo，
        // 与“画廊/查看器/设置”三个原生导航项保持同一视觉体系；展开态显示完整控件。
        PaneControls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        // 底部：收起态只显示居中的应用 logo，展开态显示状态栏
        PaneFooterExpanded.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PaneFooterCompact.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        PaneFooterCompact.Width = compact ? RootNav.CompactPaneLength : double.NaN;
    }

    /// <summary>Loads the app logo PNG (copied next to the exe) for the pane headers.</summary>
    private void SetLogoSource()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "AnimeDownloader.png");
        if (!File.Exists(logoPath))
        {
            return;
        }

        try
        {
            var logo = new BitmapImage(new Uri(logoPath));
            HeaderLogo.Source = logo;
            FooterLogo.Source = logo;
        }
        catch (Exception)
        {
            // Logo failure must not break startup.
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "gallery";
        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        if (!_pageCache.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "viewer" => new ViewerPage(),
                "settings" => new SettingsPage(),
                _ => new GalleryPage(),
            };
            _pageCache[tag] = page;
        }

        if (page is IModePage modePage)
        {
            modePage.Attach(this);
        }

        _currentPage = page;
        ContentFrame.Content = page;
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar)
        {
            return;
        }

        if (SourceCombo.SelectedItem is not IImageSource source ||
            ReferenceEquals(source, _currentSource))
        {
            return;
        }

        SetCurrentSource(source);
    }

    private void OnNsfwSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar)
        {
            return;
        }

        if (NsfwCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<NsfwMode>(item.Tag?.ToString(), out var mode) ||
            mode == _currentNsfwMode)
        {
            return;
        }

        SetCurrentNsfwMode(mode);
    }

    private void OnGlobalRefresh(object sender, RoutedEventArgs e)
    {
        if (_currentPage is IModePage modePage)
        {
            modePage.Reload();
        }
    }

    /// <summary>
    /// Shows a Win11-style confirmation dialog before clearing the thumbnail cache,
    /// including the current cache size and the impact of clearing it.
    /// </summary>
    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var sizeMb = GetThumbnailCacheSizeBytes() / (1024.0 * 1024.0);
        var sizeText = sizeMb >= 1
            ? $"{sizeMb:0.#} MB"
            : $"{Math.Max(1, (long)(sizeMb * 1024))} KB";
        var content = new TextBlock
        {
            Text = $"当前缓存约 {sizeText}（磁盘），另有少量内存缓存。\n\n" +
                   "清理后，画廊缩略图和标题栏预览需要重新联网加载；\n" +
                   "已保存下载的图片、图源标签和设置不会受影响。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        var dialog = new ContentDialog
        {
            Title = "清理缓存",
            Content = content,
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _downloader.ClearThumbnailCache();
        GlobalStatusText.Text = "缓存已清理";
    }

    private long GetThumbnailCacheSizeBytes()
    {
        var cacheDir = Path.Combine(_settingsStore.ConfigDirectory, "cache", "thumbnails");
        if (!Directory.Exists(cacheDir))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(cacheDir, "*.img", SearchOption.TopDirectoryOnly)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Current shared settings instance (pages read from this).</summary>
    public AppSettings Settings => _settings;

    public void SetCurrentSource(IImageSource source)
    {
        if (ReferenceEquals(source, _currentSource))
        {
            return;
        }

        _currentSource = source;
        _settings.SelectedSource = source.Id;
        _settingsStore.Save(_settings);
        if (SourceCombo.SelectedItem is not IImageSource selected || !ReferenceEquals(selected, source))
        {
            SyncToolbarFromSettings();
        }

        (_currentPage as IModePage)?.OnSourceChanged();
    }

    public void SetCurrentNsfwMode(NsfwMode mode)
    {
        if (mode == _currentNsfwMode)
        {
            return;
        }

        _currentNsfwMode = mode;
        _settings.NsfwMode = mode.ToStorage();
        _settingsStore.Save(_settings);
        if (NsfwCombo.SelectedItem is not ComboBoxItem item || item.Tag?.ToString() != mode.ToString())
        {
            SyncToolbarFromSettings();
        }

        (_currentPage as IModePage)?.OnNsfwChanged();
    }

    public IReadOnlyList<IImageSource> Sources => _sources;

    public IImageSource CurrentSource => _currentSource;

    public NsfwMode CurrentNsfwMode => _currentNsfwMode;

    public ImageDownloader Downloader => _downloader;

    /// <summary>Batch downloader for saving whole gallery pages.</summary>
    public BatchDownloader Batch => _batchDownloader;

    public SettingsStore SettingsStore => _settingsStore;

    /// <summary>
    /// Reloads settings, rebuilds the network layer (proxy/timeout/cache changes take effect),
    /// reapplies the theme and chrome, and notifies the current page.
    /// </summary>
    public void ApplySettings()
    {
        _settings = _settingsStore.Load();
        App.Settings = _settings;
        RebuildHttp();
        Root.RequestedTheme = App.ResolveTheme(_settings.Theme);
        Win11Chrome.Apply(this, Root);
        SyncToolbarFromSettings();
        (_currentPage as IModePage)?.OnSourceChanged();
    }

    /// <summary>
    /// Shows a small thumbnail of the currently selected image in the sidebar header.
    /// Only thumbnail bytes are downloaded (never the original), so this stays cheap.
    /// </summary>
    public void SetHeaderThumbnail(ImageItem? item)
    {
        var token = ++_headerThumbToken;
        var thumbUrl = item is null
            ? null
            : ThumbnailResolver.ResolveThumbnailUrl(
                item.ThumbnailUrl,
                item.Url,
                _settings.ThumbnailProxyTemplate);
        if (thumbUrl is null)
        {
            HeaderThumb.Source = null;
            HeaderThumbBorder.Visibility = Visibility.Collapsed;
            return;
        }

        HeaderThumbBorder.Visibility = Visibility.Visible;
        _ = LoadHeaderThumbAsync(thumbUrl, token);
    }

    private async Task LoadHeaderThumbAsync(string url, int token)
    {
        try
        {
            var bytes = await _downloader.DownloadCachedAsync(url);
            if (token != _headerThumbToken)
            {
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = 72,
                DecodePixelType = DecodePixelType.Logical,
            };
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
            HeaderThumb.Source = bitmap;
        }
        catch (Exception)
        {
            if (token == _headerThumbToken)
            {
                HeaderThumb.Source = null;
                HeaderThumbBorder.Visibility = Visibility.Collapsed;
            }
        }
    }
}

/// <summary>
/// Contract for browser-mode pages (gallery / viewer): attach to the main window, react to
/// source and NSFW changes, and support external reloads (global refresh button).
/// </summary>
public interface IModePage
{
    void Attach(MainWindow owner);

    void OnSourceChanged();

    void OnNsfwChanged();

    void Reload();
}
