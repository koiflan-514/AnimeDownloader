using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 用户设置。JSON 持久化到 <c>%LocalAppData%/AnimeDownloader/config.json</c>，
/// 键名与默认值与参考项目保持一致，便于迁移与对照。
/// </summary>
public sealed class AppSettings
{
    public const string DefaultConfigFileName = "config.json";

    public NsfwModeStorage NsfwMode { get; set; } = NsfwModeStorage.BlockNsfw;

    public bool AutoReloadEnabled { get; set; }

    public int AutoReloadIntervalSeconds { get; set; } = 30;

    public int GalleryCount { get; set; } = 12;

    public string GallerySubmode { get; set; } = "random";

    /// <summary>上次选中的图源 Id。</summary>
    public string? SelectedSource { get; set; }

    /// <summary>各图源的标签，键为图源 Id（如 "danbooru_tags"）。</summary>
    public Dictionary<string, string> SourceTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户手动代理地址，如 "http://127.0.0.1:7890"；空表示自动检测系统代理。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>true 时忽略系统代理，直连（不读取环境变量与注册表）。</summary>
    public bool UseNoProxy { get; set; }

    /// <summary>应用主题：default（跟随系统）/ light / dark。</summary>
    public string Theme { get; set; } = "default";

    /// <summary>Default directory used for batch downloads (last used).</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>Maximum concurrent downloads shared by gallery thumbnails and batch saves.</summary>
    public int MaxConcurrentDownloads { get; set; } = 4;

    /// <summary>Whether the thumbnail cache (memory LRU + disk) is enabled.</summary>
    public bool EnableThumbnailCache { get; set; } = true;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Optional thumbnail proxy template (e.g. a resize service) containing "{url}".
    /// When empty, sources without native thumbnails fall back to the original URL
    /// and the client decodes/caches a small version.
    /// </summary>
    public string? ThumbnailProxyTemplate { get; set; }
}

/// <summary>
/// 与持久化字符串一一对应的 NSFW 存储表示（"Show everything"/"Only NSFW"/"Block NSFW"）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NsfwModeStorage>))]
public enum NsfwModeStorage
{
    ShowEverything,
    OnlyNsfw,
    BlockNsfw,
}

public static class NsfwModeStorageExtensions
{
    /// <summary>存储值 → 核心枚举。</summary>
    public static Models.NsfwMode ToCore(this NsfwModeStorage storage) => storage switch
    {
        NsfwModeStorage.ShowEverything => Models.NsfwMode.ShowEverything,
        NsfwModeStorage.OnlyNsfw => Models.NsfwMode.OnlyNsfw,
        _ => Models.NsfwMode.BlockNsfw,
    };

    /// <summary>核心枚举 → 存储值。</summary>
    public static NsfwModeStorage ToStorage(this Models.NsfwMode mode) => mode switch
    {
        Models.NsfwMode.ShowEverything => NsfwModeStorage.ShowEverything,
        Models.NsfwMode.OnlyNsfw => NsfwModeStorage.OnlyNsfw,
        _ => NsfwModeStorage.BlockNsfw,
    };
}

/// <summary>
/// 配置文件的读写。线程安全；写操作立即落盘。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _configDirectory;
    private readonly string _configFile;
    private readonly object _lock = new();

    public SettingsStore(string? configDirectory = null)
    {
        _configDirectory = configDirectory
            ?? Environment.GetEnvironmentVariable("ANIMEDOWNLOADER_CONFIG_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimeDownloader");
        _configFile = Path.Combine(_configDirectory, AppSettings.DefaultConfigFileName);
    }

    /// <summary>配置文件所在目录。</summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>配置文件完整路径。</summary>
    public string ConfigFile => _configFile;

    /// <summary>
    /// 读取设置：文件不存在时返回默认设置（不落盘，首次写入时才创建目录与文件）。
    /// 文件损坏时备份原文件并返回默认设置，避免应用启动崩溃（对应参考项目
    /// 对解析异常的兜底处理）。
    /// </summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_configFile);
            AppSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                    ?? new AppSettings();
            }
            catch (JsonException)
            {
                BackupCorruptConfig(json);
                settings = new AppSettings();
            }

            settings.SourceTags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return settings;
        }
    }

    /// <summary>将无法解析的配置文件重命名为 .corrupt 备份，防止覆盖用户数据。</summary>
    private void BackupCorruptConfig(string content)
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var backup = _configFile + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.WriteAllText(backup, content);
        }
        catch (IOException)
        {
            // 备份失败不影响返回默认设置
        }
    }

    /// <summary>将设置原子写入磁盘（先写临时文件再替换，避免损坏）。</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            Directory.CreateDirectory(_configDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = _configFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _configFile, overwrite: true);
        }
    }

    /// <summary>读取指定图源的标签，未设置时返回空字符串。</summary>
    public static string GetSourceTags(AppSettings settings, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SourceTags.TryGetValue(sourceId, out var tags) ? tags ?? string.Empty : string.Empty;
    }

    /// <summary>写入指定图源的标签并保存。</summary>
    public void SetSourceTags(AppSettings settings, string sourceId, string tags)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SourceTags[sourceId] = tags ?? string.Empty;
        Save(settings);
    }
}
