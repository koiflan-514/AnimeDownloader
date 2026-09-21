using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeDownloader.App.Settings;

/// <summary>
/// Android 端特有的偏好设置。
/// </summary>
/// <remarks>
/// <para><b>为什么不把这几项并进 Core 的 <c>AppSettings</c></b>：那个类是两个桌面工程
/// 与这个 Android 工程逐字节相同的同一份代码，而
/// 「仅 Wi‑Fi 下载」「存进系统相册还是应用目录」在 Windows / Linux 上根本没有对应语义。
/// 加进去的结果是另外两个工程的配置文件里多出两个永远为 false 的字段，
/// 以及一份再也无法逐字节对齐的 Core。</para>
///
/// <para>平台特有的偏好留在平台层，这条边界本身就是「Core 中立于平台」的证据 ——
/// 一旦这条边界破了，Core 迟早会长出 <c>if (OperatingSystem.IsAndroid())</c>。</para>
/// </remarks>
internal sealed class AndroidPrefs
{
    /// <summary>只在非计费网络（Wi‑Fi / 以太网）上传输。</summary>
    public bool WifiOnlyDownloads { get; set; }

    /// <summary>true 落进系统相册（MediaStore），false 落进应用私有目录。</summary>
    public bool DownloadToGallery { get; set; } = true;

    /// <summary>系统相册中的子相册名。</summary>
    public string AlbumName { get; set; } = "AnimeDownloader";

    /// <summary>批次结束后是否保留结果通知。</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>已展示过「批量下载会转到后台继续」的提示，用户已确认。</summary>
    public bool BackgroundHintShown { get; set; }
}

/// <summary>
/// 平台偏好的读写。与 Core 的 <c>SettingsStore</c> 同目录、同套原子写盘策略
/// （先写 .tmp 再替换，避免写坏），只是文件不同。
/// </summary>
internal sealed class AndroidPrefsStore
{
    internal const string FileName = "android-prefs.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _lock = new();

    internal AndroidPrefsStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _path = Path.Combine(configDirectory, FileName);
    }

    /// <summary>偏好文件完整路径。</summary>
    internal string FilePath => _path;

    /// <summary>
    /// 读取偏好。文件不存在或损坏时返回默认值 —— 与 Core 的 <c>SettingsStore</c> 一样，
    /// 一份坏掉的配置不该让应用起不来。
    /// </summary>
    internal AndroidPrefs Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return new AndroidPrefs();
            }

            try
            {
                var json = File.ReadAllText(_path);
                var prefs = JsonSerializer.Deserialize<AndroidPrefs>(json, JsonOptions) ?? new AndroidPrefs();
                prefs.AlbumName = string.IsNullOrWhiteSpace(prefs.AlbumName)
                    ? "AnimeDownloader"
                    : prefs.AlbumName.Trim();
                return prefs;
            }
            catch (JsonException)
            {
                return new AndroidPrefs();
            }
            catch (IOException)
            {
                return new AndroidPrefs();
            }
        }
    }

    /// <summary>原子写盘。</summary>
    internal void Save(AndroidPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);

        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(prefs, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
    }
}
