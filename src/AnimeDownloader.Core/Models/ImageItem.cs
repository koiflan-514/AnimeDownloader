namespace AnimeDownloader.Core.Models;

/// <summary>
/// 单个图片条目。承载展示、保存与来源追溯所需的全部信息。
/// </summary>
/// <param name="Url">可直接下载的图片地址（原图）。</param>
/// <param name="ThumbnailUrl">缩略图地址（可能为 null；画廊缩略图优先使用，加载更快）。</param>
/// <param name="Artist">艺术家 / 作者（可能为 null）。</param>
/// <param name="SourceLink">原帖 / 作品链接（可能为 null）。</param>
/// <param name="Id">图源内的稳定标识（如 post id / pixiv pid），用于文件名建议。</param>
/// <param name="Extension">图片扩展名（不含点，可能为 null）。</param>
/// <param name="Metadata">图源返回的原始 JSON 元数据，供展示与扩展使用。</param>
public sealed record ImageItem(
    string Url,
    string? ThumbnailUrl = null,
    string? Artist = null,
    string? SourceLink = null,
    string? Id = null,
    string? Extension = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    /// <summary>
    /// Builds a suggested file name (without directory), preferring the source id and a known
    /// extension, falling back to a timestamp plus "png" when nothing can be inferred.
    /// </summary>
    public string SuggestFileName()
    {
        var baseName = string.IsNullOrWhiteSpace(Id)
            ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
            : Id;
        var ext = Extension ?? InferExtensionFromUrl() ?? "png";
        return $"{baseName}.{ext}";
    }

    /// <summary>
    /// 从 URL 末尾推断扩展名（去掉查询串），无法推断时返回 null。
    /// </summary>
    public string? InferExtensionFromUrl()
    {
        var path = Url;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var dotIndex = path.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == path.Length - 1)
        {
            return null;
        }

        var ext = path[(dotIndex + 1)..];
        return ext.Length is > 0 and <= 8 ? ext : null;
    }
}
