using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeDownloader.App;

/// <summary>
/// 主窗口：持有应用级共享状态（设置、图源、HttpClient、当前选中的图源与 NSFW 模式），
/// 通过 NavigationView 在画廊 / 查看器 / 设置三页间切换。
/// 全局工具栏（图源 / NSFW / 刷新）供两种浏览模式共享；页面通过
/// <see cref="IModePage"/> 订阅源变更并立即重载。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<IImageSource> _sources = null!;
    private ImageDownloader _downloader = null!;

    private readonly Dictionary<string, Page> _pageCache = new(StringComparer.Ordinal);
    private Page? _currentPage;

    private IImageSource _currentSource = null!;
    private NsfwMode _currentNsfwMode = NsfwMode.BlockNsfw;
    private bool _syncingToolbar;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = App.SettingsStore;
        _settings = App.Settings;
        RebuildHttp();
        Title = "AnimeDownloader";
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
        SyncToolbarFromSettings();
        ApplyWin11Chrome();
        Root.Loaded += (_, _) => ApplyWin11Chrome();
    }

    /// <summary>
    /// 应用 Win11 风格窗口外观：Mica 材质背景 + 主题自适应的标题栏按钮颜色。
    /// Mica 需要 Win11 22000+，旧系统自动回退为普通背景。
    /// </summary>
    private void ApplyWin11Chrome()
    {
        // Mica 材质（Win11 半透明背景，标题栏不再纯白）
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }

        // 标题栏按钮颜色跟随应用主题（避免系统默认的纯白/纯黑）
        var isDark = ResolveIsDark();
        var titleBar = AppWindow.TitleBar;
        var fg = isDark ? Windows.UI.Color.FromArgb(255, 235, 235, 235) : Windows.UI.Color.FromArgb(255, 20, 20, 20);
        var hover = isDark ? Windows.UI.Color.FromArgb(60, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonPressedForegroundColor = fg;
        titleBar.ButtonPressedBackgroundColor = hover;
        titleBar.ButtonInactiveForegroundColor = isDark ? Windows.UI.Color.FromArgb(140, 235, 235, 235) : Windows.UI.Color.FromArgb(140, 20, 20, 20);
    }

    /// <summary>按应用主题判断当前是否深色（Default 跟随系统，先按系统浅色处理，Loaded 后由 ActualTheme 校正）。</summary>
    private bool ResolveIsDark()
    {
        var theme = App.ResolveTheme(App.Settings.Theme);
        if (theme is ElementTheme.Light or ElementTheme.Dark)
        {
            return theme == ElementTheme.Dark;
        }

        // 跟随系统：以应用实际主题为准（窗口加载后可用）
        return Root.ActualTheme == ElementTheme.Dark;
    }

    /// <summary>按当前设置重建 HttpClient、图源与下载器（代理/直连变更后调用）。</summary>
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
        _downloader = new ImageDownloader(_http);
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
            // 图源下拉
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

            // NSFW 下拉
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

    /// <summary>当前共享设置实例（避免页面重复读盘）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>切换当前图源并通知各模式页面。</summary>
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

    /// <summary>切换 NSFW 模式并通知各模式页面。</summary>
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

    /// <summary>当前全部图源实例。</summary>
    public IReadOnlyList<IImageSource> Sources => _sources;

    /// <summary>当前选中的图源。</summary>
    public IImageSource CurrentSource => _currentSource;

    /// <summary>当前 NSFW 模式。</summary>
    public NsfwMode CurrentNsfwMode => _currentNsfwMode;

    /// <summary>图片下载器（保存 / 批量下载）。</summary>
    public ImageDownloader Downloader => _downloader;

    /// <summary>设置存储。</summary>
    public SettingsStore SettingsStore => _settingsStore;

    /// <summary>设置页保存后调用：重载内存设置、重建网络层（代理生效）、应用主题并同步工具栏。</summary>
    public void ApplySettings()
    {
        _settings = _settingsStore.Load();
        App.Settings = _settings;
        RebuildHttp();
        Root.RequestedTheme = App.ResolveTheme(_settings.Theme);
        ApplyWin11Chrome();
        SyncToolbarFromSettings();
        (_currentPage as IModePage)?.OnSourceChanged();
    }
}

/// <summary>
/// 浏览模式页面（画廊 / 查看器）需要实现的契约：附加到主窗口、响应源与
/// NSFW 变更、支持外部触发重载（全局刷新按钮）。
/// </summary>
public interface IModePage
{
    /// <summary>绑定主窗口共享状态（导航到该页时调用）。</summary>
    void Attach(MainWindow owner);

    /// <summary>当前图源变更后的响应（立即重载）。</summary>
    void OnSourceChanged();

    /// <summary>NSFW 模式变更后的响应（立即重载）。</summary>
    void OnNsfwChanged();

    /// <summary>按当前设置重载（全局刷新按钮）。</summary>
    void Reload();
}
