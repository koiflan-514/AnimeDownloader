using AnimeDownloader.App.Platform;
using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace AnimeDownloader.App;

/// <summary>
/// 主窗口：持有全应用共享状态（设置、图源、HttpClient、当前图源与 NSFW 模式），
/// 并在画廊 / 灯箱 / 设置三个页面之间切换。56px 图标导轨只负责导航；
/// 内容区上方的常驻命令条负责图源选择、NSFW 过滤、状态灯与刷新 / 清缓存。
/// 页面通过 <see cref="IModePage"/> 响应变化。
///
/// 实现 <see cref="IDisposable"/> 是因为它持有共享的 <see cref="HttpClient"/> 与
/// <see cref="ImageDownloader"/>（前者还带连接池、后者带磁盘缓存与并发闸）。
/// 窗口关闭是这些资源唯一的收尾点，顺带把缓存页里实现了 <see cref="IDisposable"/>
/// 的页面（灯箱持有整张解码位图）一起放掉。
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<IImageSource> _sources = null!;
    private ImageDownloader _downloader = null!;
    private BatchDownloader _batchDownloader = null!;

    private readonly Dictionary<string, Control> _pageCache = new(StringComparer.Ordinal);
    private Control? _currentPage;

    private IImageSource _currentSource = null!;
    private NsfwMode _currentNsfwMode = NsfwMode.BlockNsfw;
    private bool _syncingToolbar;
    private int _headerThumbToken;
    private string _statusText = "就绪";
    private bool _statusBusy;
    private bool _statusError;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = App.SettingsStore;
        _settings = App.Settings;
        RebuildHttp();

        RequestedThemeVariant = App.ResolveTheme(_settings.Theme);
        SetVersionText();
        SyncToolbarFromSettings();
        SetLogoSource();

        // 首屏尺寸与最小尺寸在构造期就给：Avalonia 的 MinWidth/MinHeight 是 DIP，
        // 不需要 WinUI 那套 XamlRoot.RasterizationScale 换算。
        WindowChrome.Apply(this, withInitialBounds: true);

        SourceCombo.SelectionChanged += OnSourceSelectionChanged;
        NsfwSegmented.SelectionChanged += OnNsfwSelectionChanged;
        WireRail(GalleryRailItem, "gallery");
        WireRail(ViewerRailItem, "viewer");
        WireRail(SettingsRailItem, "settings");
        KeyDown += OnShellKeyDown;

        // 标题栏要等窗口句柄真的存在之后才能着色，Opened 里补一次。
        Opened += OnShellOpened;
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
            SourceCombo.ItemsSource = _sources;
            if (NsfwSegmented.ItemsSource is null)
            {
                NsfwSegmented.ItemsSource = new List<string> { "屏蔽 NSFW", "仅 NSFW", "全部" };
            }

            for (var i = 0; i < _sources.Count; i++)
            {
                if (ReferenceEquals(_sources[i], _currentSource))
                {
                    SourceCombo.SelectedIndex = i;
                    break;
                }
            }

            _currentNsfwMode = _settings.NsfwMode.ToCore();
            NsfwSegmented.SelectedIndex = ModeToIndex(_currentNsfwMode);
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

    private static int ModeToIndex(NsfwMode mode) => mode switch
    {
        NsfwMode.OnlyNsfw => 1,
        NsfwMode.ShowEverything => 2,
        _ => 0,
    };

    private void SetVersionText()
    {
        try
        {
            var assembly = typeof(MainWindow).Assembly;
            var version = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(assembly.Location)
                .FileVersion;
            VersionText.Text = string.IsNullOrWhiteSpace(version)
                ? string.Empty
                : $"v{version}";
        }
        catch (Exception)
        {
            // 版本读取失败不影响启动。
        }
    }

    /// <summary>
    /// 更新命令条上的状态指示：文本 + 三档状态点（安静灰 / 工作=强调色 / 故障红）。
    /// </summary>
    public void SetGlobalStatus(string text, bool busy = false, bool error = false)
    {
        _statusText = text;
        _statusBusy = busy;
        _statusError = error;
        ApplyGlobalStatus();
    }

    private void ApplyGlobalStatus()
    {
        GlobalStatusText.Text = _statusText;
        // 三档互斥：故障优先于工作。Avalonia 侧用伪类而不是「去 Resource 里取 Style」——
        // 后者是 WinUI 的写法（Style 可以当值赋给控件），Avalonia 的 ControlTheme 不能这么换。
        StatusDot.Classes.Set("error", _statusError);
        StatusDot.Classes.Set("busy", _statusBusy && !_statusError);
    }

    /// <summary>加载应用标记（与 exe 一起复制到输出目录的 PNG）。</summary>
    private void SetLogoSource()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "AnimeDownloader.png");
        if (!File.Exists(logoPath))
        {
            return;
        }

        try
        {
            FooterLogo.Source = new Bitmap(logoPath);
        }
        catch (Exception)
        {
            // Logo 失败不影响启动。
        }
    }

    /// <summary>
    /// 首帧：选中「画廊」并落到内容区。
    /// 导轨项现在两条路都接了导航（Click + IsCheckedChanged，见 WireRail），
    /// 所以这里设 IsChecked 本身就会把画廊页推上去；下面的显式兜底保留，
    /// 是因为「首屏必须有内容」这条不该依赖某个控件的事件时序。
    /// </summary>
    private void OnShellOpened(object? sender, EventArgs e)
    {
        // 句柄就绪后补一次标题栏着色（构造期 TryGetPlatformHandle 还拿不到句柄）。
        WindowChrome.ApplyTitleBar(this);

        if (GalleryRailItem.IsChecked != true)
        {
            GalleryRailItem.IsChecked = true;
        }

        if (_currentPage is null)
        {
            NavigateTo("gallery");
        }
    }

    // ==================== 收尾 ====================

    /// <summary>
    /// 释放共享资源：缓存页 → 下载器 → HttpClient（顺序即依赖方向，
    /// 下载器要等页面不再向它派活之后才关）。
    /// </summary>
    public void Dispose()
    {
        ContentFrame.Content = null;
        _currentPage = null;
        foreach (var page in _pageCache.Values)
        {
            if (page is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _pageCache.Clear();
        _downloader.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// 给一个导轨项接上导航。两条路都必须接：
    ///
    /// · <c>Click</c> —— 鼠标点 / 键盘（空格、回车）激活时的即时响应，与 WinUI 的手感一致。
    /// · <c>IsCheckedChanged</c> —— 选中态被**别的途径**改变时也要跟着切页：方向键在
    ///   同一个 GroupName 组内移动选中项、辅助功能层调 SelectionItemPattern.Select()、
    ///   以及程序化设置 IsChecked，都不会触发 Click。只接 Click 的话会出现
    ///   「导轨上勾选已经挪到设置、内容区还停在画廊」这种自相矛盾的状态。
    ///   （本机用 UIA 的 SelectionItemPattern.Select() 实测复现过：Select() 成功返回、
    ///   勾选态确实变了，但页面纹丝不动。）
    ///
    /// 两条路同时命中不会重复做事：对同一页 NavigateTo 只是重新 Attach 一次。
    /// </summary>
    private void WireRail(RadioButton item, string tag)
    {
        item.Click += (_, _) => NavigateTo(tag);
        item.IsCheckedChanged += (_, _) =>
        {
            if (item.IsChecked == true)
            {
                NavigateTo(tag);
            }
        };
    }

    private void NavigateTo(string tag)
    {
        // 全屏状态下换页（比如在灯箱里点标签跳回画廊）必须先把外壳恢复：
        // 导轨与命令条是全屏时收起的，不恢复的话新页面上方会永远缺一条命令条，
        // 而且没有任何入口能再退回来。
        if (IsFullscreen)
        {
            SetFullscreen(false);
        }

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

    /// <summary>从上下文引导动作跳到设置页。</summary>
    public void NavigateToSettings()
    {
        SettingsRailItem.IsChecked = true;
        NavigateTo("settings");
    }

    // ==================== 全屏（F11） ====================

    /// <summary>窗口当前是否全屏。</summary>
    public bool IsFullscreen => WindowChrome.IsFullscreen(this);

    /// <summary>切换整窗全屏。查看器等页面通过它进入全屏，而不是自己去动窗口状态。</summary>
    public void ToggleFullscreen() => SetFullscreen(!IsFullscreen);

    /// <summary>退出全屏（已经不是全屏时是空操作）。</summary>
    public void ExitFullscreen() => SetFullscreen(false);

    /// <summary>
    /// 整窗全屏开关。注意「全屏」是**外壳**的能力：导轨与命令条挂在 MainWindow 上，
    /// 不归页面管。之前只有查看器收起了自己的页内控件，于是全屏之后左边 56px 导轨
    /// 和顶上命令条原地不动 —— 窗口是满屏了，画面却仍被侧栏挤着。
    /// </summary>
    public void SetFullscreen(bool on)
    {
        if (on == IsFullscreen)
        {
            return;
        }

        WindowChrome.SetFullscreen(this, on);
        ApplyImmersiveChrome(on);
    }

    private void ApplyImmersiveChrome(bool immersive)
    {
        // 导轨列宽必须跟着归零：只把 Border 隐藏掉的话，56px 的列还在，内容区依旧被挤。
        // （Avalonia 的 IsVisible=false 同样不改变列宽 —— 这个坑两边一样。）
        Root.ColumnDefinitions[0].Width = immersive ? new GridLength(0) : new GridLength(56);
        RailHost.IsVisible = !immersive;
        CommandBarHost.IsVisible = !immersive;
        (_currentPage as IImmersivePage)?.SetImmersive(immersive);
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        // F11 走窗口级 KeyDown：路由事件从焦点元素冒泡到窗口，焦点在哪儿都能收到。
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleFullscreen();
            return;
        }

        // Esc 只在全屏状态接管。无条件吞掉的话，会抢走 ComboBox / 弹层自己的 Esc。
        if (e.Key == Key.Escape && IsFullscreen)
        {
            e.Handled = true;
            ExitFullscreen();
        }
    }

    /// <summary>
    /// 跳转到「标签视图」：把当前图源的标签设为指定标签并回到画廊刷新。
    /// 由查看器的标签 chip 点击触发（点击图片上的任一标签即可浏览该标签下的图片）。
    /// </summary>
    public void NavigateToTagView(string tag)
    {
        var cleaned = tag.Trim();
        if (cleaned.Length == 0 || !_currentSource.SupportsTags)
        {
            return;
        }

        _currentSource.Tags = cleaned;
        _settings.SourceTags[_currentSource.Id] = cleaned;
        _settingsStore.Save(_settings);
        GalleryRailItem.IsChecked = true;
        NavigateTo("gallery");
        (_currentPage as IModePage)?.OnSourceChanged();
        SetGlobalStatus($"已切换到标签视图：{cleaned}");
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
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

    private void OnNsfwSelectionChanged(object? sender, int index)
    {
        if (_syncingToolbar)
        {
            return;
        }

        var mode = index switch
        {
            1 => NsfwMode.OnlyNsfw,
            2 => NsfwMode.ShowEverything,
            _ => NsfwMode.BlockNsfw,
        };
        if (mode == _currentNsfwMode)
        {
            return;
        }

        SetCurrentNsfwMode(mode);
    }

    private void OnGlobalRefresh(object? sender, RoutedEventArgs e)
    {
        if (_currentPage is IModePage modePage)
        {
            modePage.Reload();
        }
    }

    /// <summary>
    /// 清理缩略图缓存前先确认，并说明当前缓存体积与影响范围。
    /// </summary>
    private async void OnClearCacheClick(object? sender, RoutedEventArgs e)
    {
        var sizeMb = GetThumbnailCacheSizeBytes() / (1024.0 * 1024.0);
        var sizeText = sizeMb >= 1
            ? $"{sizeMb:0.#} MB"
            : $"{Math.Max(1, (long)(sizeMb * 1024))} KB";
        ClearCacheMessage.Text =
            $"当前缓存约 {sizeText}（磁盘），另有少量内存缓存。\n\n" +
            "清理后，画廊缩略图和标题栏预览需要重新联网加载；\n" +
            "已保存下载的图片、图源标签和设置不会受影响。";

        if (!await ClearCacheDialog.ShowAsync())
        {
            return;
        }

        _downloader.ClearThumbnailCache();
        SetGlobalStatus("缓存已清理");
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

    /// <summary>当前共享设置实例（页面读这个）。</summary>
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
        if (NsfwSegmented.SelectedIndex != ModeToIndex(mode))
        {
            SyncToolbarFromSettings();
        }

        (_currentPage as IModePage)?.OnNsfwChanged();
    }

    public IReadOnlyList<IImageSource> Sources => _sources;

    public IImageSource CurrentSource => _currentSource;

    public NsfwMode CurrentNsfwMode => _currentNsfwMode;

    public ImageDownloader Downloader => _downloader;

    /// <summary>整页批量下载用的批量下载器。</summary>
    public BatchDownloader Batch => _batchDownloader;

    public SettingsStore SettingsStore => _settingsStore;

    /// <summary>
    /// 重新读取设置、重建网络层（代理 / 超时 / 缓存改动需要重新建 HttpClient）、
    /// 重上主题与外壳，并通知当前页面。
    /// </summary>
    public void ApplySettings()
    {
        _settings = _settingsStore.Load();
        App.Settings = _settings;
        RebuildHttp();
        RequestedThemeVariant = App.ResolveTheme(_settings.Theme);
        WindowChrome.ApplyTitleBar(this);
        ApplyGlobalStatus();
        SyncToolbarFromSettings();
        (_currentPage as IModePage)?.OnSourceChanged();
    }

    /// <summary>
    /// 在命令条上显示当前选中图片的小缩略图。
    /// 只下载缩略图字节（绝不下载原图），因此这个动作始终很便宜。
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
            HeaderThumbBorder.IsVisible = false;
            return;
        }

        HeaderThumbBorder.IsVisible = true;
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

            using var stream = new MemoryStream(bytes);
            HeaderThumb.Source = Bitmap.DecodeToWidth(stream, 72, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            if (token == _headerThumbToken)
            {
                HeaderThumb.Source = null;
                HeaderThumbBorder.IsVisible = false;
            }
        }
    }
}
