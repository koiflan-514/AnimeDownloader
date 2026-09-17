using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimeDownloader.App;

/// <summary>
/// Main window: owns app-wide shared state (settings, sources, HttpClient, selected source and
/// NSFW mode) and switches between the gallery / viewer / settings pages. The 56px icon rail
/// only carries navigation; the always-visible command bar above the frame owns the source
/// picker, the NSFW filter, the status beacon and the refresh / cache actions. Pages react to
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
    private string _statusText = "就绪";
    private bool _statusBusy;
    private bool _statusError;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = App.SettingsStore;
        _settings = App.Settings;
        RebuildHttp();
        Title = "AnimeDownloader";
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
        SetVersionText();
        SyncToolbarFromSettings();
        SetLogoSource();
        Win11Chrome.SetIcon(this);
        Win11Chrome.Apply(this, Root);
        Root.Loaded += (_, _) =>
        {
            Win11Chrome.Apply(this, Root);
            // 首屏尺寸必须在 Loaded 之后给：需要 XamlRoot 的 DPI 换算，
            // 在此之前窗口还是系统默认尺寸（默认尺寸下命令条基本是坏的）。
            Win11Chrome.SetInitialBounds(this, Root);
        };
        // 跟随用户 Windows 配色：注册主题根 + 标题栏随亮暗切换重上色。
        WindowsColorScheme.Instance.Register(Root);
        Root.ActualThemeChanged += (_, _) => Win11Chrome.ApplyAfterThemeChange(this, Root);
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

            if (NsfwSegmented.ItemsSource is null)
            {
                NsfwSegmented.ItemsSource = new List<string> { "屏蔽 NSFW", "仅 NSFW", "全部" };
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
            NsfwSegmented.SelectedIndex = _currentNsfwMode switch
            {
                NsfwMode.OnlyNsfw => 1,
                NsfwMode.ShowEverything => 2,
                _ => 0,
            };
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
    /// 更新侧栏底部状态胶囊：文本 + 颜色状态点（就绪绿 / 忙碌橙 / 错误红）。
    /// 主题切换时按保存的状态重新上色。
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
        var styleKey = _statusError
            ? "AppStatusDotErrorStyle"
            : _statusBusy ? "AppStatusDotBusyStyle" : "AppStatusDotOkStyle";
        StatusDot.Style = (Style)Application.Current.Resources[styleKey];
    }

    /// <summary>Loads the app logo PNG (copied next to the exe) for the rail mark.</summary>
    private void SetLogoSource()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "AnimeDownloader.png");
        if (!File.Exists(logoPath))
        {
            return;
        }

        try
        {
            FooterLogo.Source = new BitmapImage(new Uri(logoPath));
        }
        catch (Exception)
        {
            // Logo failure must not break startup.
        }
    }

    /// <summary>
    /// 首帧：选中「画廊」并落到内容区。RadioButton 的 Checked 只在状态真正翻转时触发，
    /// 因此这里同时兜底一次直接导航，避免启动时停在空 Frame 上。
    /// </summary>
    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        if (GalleryRailItem.IsChecked != true)
        {
            GalleryRailItem.IsChecked = true; // 触发 OnRailItemChecked → NavigateTo("gallery")
            return;
        }

        if (_currentPage is null)
        {
            NavigateTo("gallery");
        }
    }

    private void OnRailItemChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton item &&
            item.Tag?.ToString() is { Length: > 0 } tag)
        {
            NavigateTo(tag);
        }
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

    /// <summary>Navigates to settings from contextual onboarding actions.</summary>
    public void NavigateToSettings()
    {
        SettingsRailItem.IsChecked = true;
        NavigateTo("settings");
    }

    // ==================== 全屏（F11） ====================

    /// <summary>窗口当前是否全屏。</summary>
    public bool IsFullscreen => Win11Chrome.IsFullscreen(this);

    /// <summary>切换整窗全屏。查看器等页面通过它进入全屏，而不是自己去动 AppWindow。</summary>
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

        Win11Chrome.SetFullscreen(this, Root, on);
        ApplyImmersiveChrome(on);
    }

    private void ApplyImmersiveChrome(bool immersive)
    {
        // 导轨列宽必须跟着归零：只把 Border 折叠掉的话，56px 的列还在，内容区依旧被挤。
        RailColumn.Width = immersive ? new GridLength(0) : new GridLength(56);
        RailHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        CommandBarHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        (_currentPage as IImmersivePage)?.SetImmersive(immersive);
    }

    private void OnFullscreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullscreen();
    }

    private void OnShellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 只认全屏状态下的 Esc，其余情况一律放行 —— 否则会抢掉弹层 / 下拉框的 Esc。
        if (e.Key == Windows.System.VirtualKey.Escape && IsFullscreen)
        {
            e.Handled = true;
            ExitFullscreen();
        }
    }

    /// <summary>
    /// 跳转到"标签视图"：把当前图源的标签设为指定标签并回到画廊刷新。
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

    private void OnNsfwSelectionChanged(object sender, int index)
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
        ApplyGlobalStatus();
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

/// <summary>
/// 整窗全屏时页面收起 / 恢复自己的页内控件。外壳的导轨与命令条由 <see cref="MainWindow"/>
/// 自己处理；页面只负责自己那一层（例如查看器的浮动工具栏、状态条、标签区、内边距）。
/// </summary>
public interface IImmersivePage
{
    void SetImmersive(bool immersive);
}