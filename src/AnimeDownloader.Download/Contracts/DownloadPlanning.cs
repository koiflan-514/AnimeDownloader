using System.Text;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Download.Contracts;

/// <summary>
/// 一次落盘的文件描述。文件名已经过净化与批内去重，可以直接交给 Android 存储层。
/// </summary>
/// <param name="DisplayName">最终文件名（含扩展名，不含目录）。</param>
/// <param name="MimeType">推断出的 MIME（如 <c>image/jpeg</c>）；无法判断时为 null，由存储层按扩展名自行判定。</param>
/// <param name="AlbumName">目标子相册名（如 <c>AnimeDownloader</c>）。</param>
/// <param name="SourceUrl">源地址，仅用于日志与失败归因。</param>
public sealed record DownloadFileSpec(
    string DisplayName,
    string? MimeType,
    string AlbumName,
    string SourceUrl);

/// <summary>
/// 落点抽象。模块只通过这个接口接触存储，因此「写进系统相册（MediaStore）」
/// 与「写进应用私有/公共目录（API 28 及以下）」是同一个算法下的两种实现。
/// </summary>
/// <remarks>
/// 契约中最重要的一条是<b>原子性</b>：<see cref="WriteAsync"/> 要么让内容完整可见，
/// 要么什么都不留下。半成品绝不能出现在用户的相册里。
/// </remarks>
public interface IDownloadTarget
{
    /// <summary>给用户看的落点说明，如「相册 / AnimeDownloader」。用于状态栏与结果提示。</summary>
    string DisplayLocation { get; }

    /// <summary>目标位置是否已存在同名非空文件。</summary>
    Task<bool> ExistsAsync(DownloadFileSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// 把 <paramref name="source"/> 的内容完整写入目标并提交，返回最终落点
    /// （Content URI 字符串或绝对路径）。失败时抛出异常，且不留下可见的半成品。
    /// </summary>
    Task<string> WriteAsync(DownloadFileSpec spec, Stream source, CancellationToken cancellationToken);

    /// <summary>目标所在卷的可用字节数。</summary>
    Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 文件名净化与类型推断。全部是纯函数 —— 没有 Android 类型，
/// 因此可以在桌面测试工程里直接断言（见 <c>tests/AnimeDownloader.Download.Tests</c>）。
/// </summary>
public static class DownloadFileNaming
{
    /// <summary>
    /// 文件名长度上限（按<b>字符</b>计）。ext4 的单个文件名上限是 255 <b>字节</b>，
    /// 而图源给的 id 可能很长、中文名一个字符占 3 字节 —— 取 120 字符留足余量，
    /// 截断时保留扩展名。
    /// </summary>
    public const int MaxNameLength = 120;

    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n', '\t', '\0'];

    /// <summary>
    /// 把任意来源的字符串净化为安全文件名：替换非法字符、压掉重复空格、
    /// 去掉首尾的点与空格（Windows 与部分 MTP 实现都不接受）、限制长度并保留扩展名。
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultName();
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(InvalidChars, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
        }

        var collapsed = new StringBuilder(builder.Length);
        var lastWasSpace = false;
        foreach (var ch in builder.ToString())
        {
            var isSpace = ch == ' ';
            if (isSpace && lastWasSpace)
            {
                continue;
            }

            collapsed.Append(ch);
            lastWasSpace = isSpace;
        }

        var result = collapsed.ToString().Trim().Trim('.');
        if (result.Length == 0)
        {
            return DefaultName();
        }

        return TruncatePreservingExtension(result, MaxNameLength);
    }

    /// <summary>
    /// 保证文件名带扩展名：已有则原样返回，没有则补上 <paramref name="fallbackExtension"/>
    /// （缺省 <c>png</c>，与 <see cref="ImageItem.SuggestFileName"/> 的兜底一致）。
    /// </summary>
    public static string EnsureExtension(string name, string? fallbackExtension = null)
    {
        var sanitized = Sanitize(name);
        var ext = GetExtension(sanitized);
        if (!string.IsNullOrEmpty(ext))
        {
            return sanitized;
        }

        var fallback = string.IsNullOrWhiteSpace(fallbackExtension)
            ? "png"
            : fallbackExtension.Trim().TrimStart('.').ToLowerInvariant();
        return $"{sanitized}.{fallback}";
    }

    /// <summary>取小写扩展名（不含点）；没有扩展名时返回 null。</summary>
    public static string? GetExtension(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return null;
        }

        var ext = name[(dot + 1)..];
        // 图源偶尔把查询串带进文件名（"a.php?i=1"），这类「扩展名」不是扩展名。
        return ext.Length is > 0 and <= 8 && ext.All(char.IsLetterOrDigit) ? ext.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 由扩展名推断 MIME。无法判定时返回 <c>null</c> —— 此时存储层不写 MIME 列，
    /// 交给 MediaStore 按文件名自行判定，比猜一个 <c>image/jpeg</c> 更诚实。
    /// </summary>
    public static string? InferMimeType(string? fileName) => GetExtension(fileName) switch
    {
        "jpg" or "jpeg" or "jpe" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "avif" => "image/avif",
        "bmp" => "image/bmp",
        "heic" => "image/heic",
        "heif" => "image/heif",
        "jxl" => "image/jxl",
        "tif" or "tiff" => "image/tiff",
        "svg" => "image/svg+xml",
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        _ => null,
    };

    /// <summary>扩展名是否为已知的图片类型（用于在保存前挡掉明显不是图片的响应）。</summary>
    public static bool LooksLikeImage(string? fileName) =>
        InferMimeType(fileName)?.StartsWith("image/", StringComparison.Ordinal) == true;

    /// <summary>兜底文件名：时间戳 + png，与 <see cref="ImageItem.SuggestFileName"/> 的兜底同构。</summary>
    private static string DefaultName() =>
        $"image_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";

    /// <summary>超长时截断主体并保留扩展名。</summary>
    private static string TruncatePreservingExtension(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var ext = GetExtension(name);
        if (ext is null)
        {
            return name[..maxLength];
        }

        var suffix = "." + ext;
        var keep = Math.Max(1, maxLength - suffix.Length);
        return name[..keep] + suffix;
    }
}

/// <summary>
/// 把一批 <see cref="ImageItem"/> 规划成一组可直接落盘的 <see cref="DownloadFileSpec"/>：
/// 净化文件名、补全扩展名、批内去重。纯函数，可单测。
/// </summary>
public static class DownloadPlanner
{
    /// <summary>默认相册名（与 <see cref="DownloadBatchOptions.AlbumName"/> 的默认值保持一致）。</summary>
    public const string DefaultAlbumName = "AnimeDownloader";

    /// <summary>
    /// 规划落盘文件。返回的列表与入参<b>一一对应</b>（同下标同图），
    /// 这样进度快照的 <c>Index</c> 可以直接对回原列表。
    /// </summary>
    public static IReadOnlyList<DownloadFileSpec> Plan(
        IReadOnlyList<ImageItem> items,
        string? albumName = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var album = string.IsNullOrWhiteSpace(albumName) ? DefaultAlbumName : albumName.Trim();
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var specs = new List<DownloadFileSpec>(items.Count);
        foreach (var item in items)
        {
            var name = Deduplicate(
                DownloadFileNaming.EnsureExtension(item.SuggestFileName(), item.InferExtensionFromUrl()),
                used);
            specs.Add(new DownloadFileSpec(name, DownloadFileNaming.InferMimeType(name), album, item.Url));
        }

        return specs;
    }

    /// <summary>
    /// 批内去重：重名时插入 <c> (2)</c>、<c> (3)</c> …，且保留扩展名在最后。
    /// 计数按<b>小写</b>比较 —— Android 的外部存储多是大小写不敏感的（FAT/exFAT 挂载、
    /// 部分 MTP 实现），只按序数比较会漏掉 <c>A.png</c> 与 <c>a.png</c> 的冲突。
    /// </summary>
    private static string Deduplicate(string name, Dictionary<string, int> used)
    {
        if (!used.TryGetValue(name, out var count))
        {
            used[name] = 1;
            return name;
        }

        var ext = DownloadFileNaming.GetExtension(name);
        var stem = ext is null ? name : name[..^(ext.Length + 1)];
        string candidate;
        do
        {
            count++;
            candidate = ext is null ? $"{stem} ({count})" : $"{stem} ({count}).{ext}";
        }
        while (used.ContainsKey(candidate));

        used[name] = count;
        used[candidate] = 1;
        return candidate;
    }
}
