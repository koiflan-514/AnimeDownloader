using Android.Content;
using AnimeDownloader.Download;
using AnimeDownloader.Download.Contracts;
using AnimeDownloader.App.Design;
using AnimeDownloader.App.Platform;
using AnimeDownloader.App.Settings;
using AnimeDownloader.App.Thumbnails;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.App;

/// <summary>
/// 组合根：应用内所有长生命周期对象在这里创建一次，其余地方只读不建。
/// </summary>
/// <remarks>
/// <para><b>为什么必须集中</b>：<see cref="ImageDownloader"/> 的并发闸门是<b>实例状态</b>。
/// 如果画廊缩略图与批量下载各建一个下载器，它们会各自开满一份并发预算 ——
/// 用户看到的是「明明设了并发 3，实际跑了 6 条连接」。共用一个实例才让
/// 「最大并发下载数」这个设置在两处都名副其实。</para>
///
/// <para>生命周期挂在 <c>Application</c> 上而不是 Activity 上：批量下载会在用户离开界面、
/// 甚至锁屏之后继续跑，此时 Activity 可能已经被销毁重建，而这些对象必须还活着。</para>
/// </remarks>
internal sealed class AppServices : IDisposable
{
    private readonly Context _context;
    private AndroidDownloadModule? _downloadModule;
    private bool _disposed;

    private AppServices(
        Context context,
        SettingsStore store,
        AppSettings settings,
        AndroidPrefsStore prefsStore,
        AndroidPrefs prefs)
    {
        _context = context;
        SettingsStore = store;
        Settings = settings;
        PrefsStore = prefsStore;
        Prefs = prefs;

        var httpFactory = new HttpClientFactory(settings);
        HttpClient = httpFactory.CreateClient();

        // 缩略图的磁盘缓存放应用缓存目录：系统在空间紧张时会自行回收，
        // 不需要（也不应该）自己写一套清理策略。
        var cacheDirectory = Path.Combine(
            context.CacheDir?.AbsolutePath ?? context.FilesDir!.AbsolutePath!,
            "thumbnails");

        Downloader = new ImageDownloader(HttpClient, new ImageDownloaderOptions
        {
            MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 8),
            CacheDirectory = settings.EnableThumbnailCache ? cacheDirectory : null,
            EnableThumbnailCache = settings.EnableThumbnailCache,
        });
        Batch = new BatchDownloader(Downloader);
        Thumbnails = new ThumbnailLoader(Downloader);
        Sources = SourceRegistry.CreateAll(HttpClient);
        SourceRegistry.ApplySettings(Sources, settings);

        CurrentSourceId = settings.SelectedSource;
    }

    /// <summary>Core 的设置存储（平台中立的那一半）。</summary>
    internal SettingsStore SettingsStore { get; }

    /// <summary>Core 的设置（可变，改动后由 <see cref="SaveSettings"/> 落盘）。</summary>
    internal AppSettings Settings { get; }

    /// <summary>Android 平台偏好的存储。</summary>
    internal AndroidPrefsStore PrefsStore { get; }

    /// <summary>Android 平台偏好（仅 Wi‑Fi、落点、相册名……）。</summary>
    internal AndroidPrefs Prefs { get; }

    /// <summary>应用内共享的 HttpClient（统一 User-Agent / 超时 / 代理隧道）。</summary>
    internal HttpClient HttpClient { get; }

    /// <summary>共享的图片下载器（缩略图与批量下载共用一份并发预算）。</summary>
    internal ImageDownloader Downloader { get; }

    /// <summary>批量下载器（Core 提供，与桌面端同一份实现）。</summary>
    internal BatchDownloader Batch { get; }

    /// <summary>缩略图加载器（采样解码 + 位图 LRU + 请求合并）。</summary>
    internal ThumbnailLoader Thumbnails { get; }

    /// <summary>全部内置图源。</summary>
    internal IReadOnlyList<IImageSource> Sources { get; }

    /// <summary>当前选中的图源 Id。</summary>
    internal string? CurrentSourceId { get; set; }

    /// <summary>当前图源。取不到时回落到第一个。</summary>
    internal IImageSource CurrentSource =>
        Sources.FirstOrDefault(s => s.Id == CurrentSourceId) ?? Sources[0];

    /// <summary>当前的 NSFW 过滤模式。</summary>
    internal NsfwMode CurrentNsfwMode => Settings.NsfwMode.ToCore();

    /// <summary>当前主题模式。</summary>
    internal AppThemeMode ThemeMode => Settings.Theme switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };

    /// <summary>
    /// 本次下载批次使用的策略。默认值即「手机上的保守选择」。
    /// </summary>
    internal DownloadBatchOptions BuildDownloadOptions() => new()
    {
        AlbumName = Prefs.AlbumName,
        WifiOnly = Prefs.WifiOnlyDownloads,
        Location = Prefs.DownloadToGallery ? DownloadLocation.Gallery : DownloadLocation.AppPrivate,
        // 并发交给设备自适应：低内存机型上开满连接会被系统直接回收进程。
        MaxConcurrentDownloads = 0,
        OverwriteExisting = false,
        NotifyOnCompletion = Prefs.NotifyOnCompletion,
    };

    /// <summary>把内存中的设置写回磁盘（Core 设置 + 平台偏好）。</summary>
    internal void SaveSettings()
    {
        Settings.SelectedSource = CurrentSourceId;
        SettingsStore.Save(Settings);
        PrefsStore.Save(Prefs);
    }

    /// <summary>
    /// 创建下载模块。宿主界面类型用于「点通知回到应用」。
    /// </summary>
    /// <remarks>
    /// 只在首次调用时创建（宿主 Activity 在 <c>OnCreate</c> 里调一次）。
    /// 之所以不在 <see cref="Create"/> 里就建好：那一刻还没有任何界面，
    /// 而模块需要知道点通知该拉起谁 —— 那个信息只有 Activity 知道。
    /// </remarks>
    internal AndroidDownloadModule CreateDownloadModule(Type launcherActivity)
    {
        ArgumentNullException.ThrowIfNull(launcherActivity);

        // 已经建过就复用：模块内部持有「当前批次」这一状态，
        // 每次重建都会把正在跑（或刚跑完）的进度丢掉。
        return _downloadModule ??= new AndroidDownloadModule(_context, Downloader, launcherActivity);
    }

    /// <summary>在 Application 启动时创建。</summary>
    internal static AppServices Create(Context applicationContext)
    {
        ArgumentNullException.ThrowIfNull(applicationContext);

        // 配置目录显式落在应用私有目录：Android 上没有「%LocalAppData%」这种概念，
        // 而 SettingsStore 的默认实现会去问 Environment.SpecialFolder.LocalApplicationData ——
        // 那个值在 Android 上随实现而变，不如自己钉死。
        var configDirectory = Path.Combine(
            applicationContext.FilesDir?.AbsolutePath ?? applicationContext.CacheDir!.AbsolutePath!,
            "config");
        var store = new SettingsStore(configDirectory);
        var prefsStore = new AndroidPrefsStore(configDirectory);
        return new AppServices(applicationContext, store, store.Load(), prefsStore, prefsStore.Load());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloadModule?.Dispose();
        Thumbnails.Dispose();
        Downloader.Dispose();
        HttpClient.Dispose();
    }
}
