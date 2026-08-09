using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// 图源适配器抽象。UI 只依赖此接口，新增图源时实现该接口并注册即可。
/// 对应参考项目 CatgirlDownloader 的 BaseDownloaderAPI。
/// </summary>
public interface IImageSource
{
    /// <summary>稳定标识（用于配置键与下拉选择），如 "danbooru"。</summary>
    string Id { get; }

    /// <summary>界面显示名，如 "Danbooru"。</summary>
    string DisplayName { get; }

    /// <summary>界面显示的一句话描述。</summary>
    string Description { get; }

    /// <summary>是否支持确定性分页浏览（决定画廊分页控件是否显示）。</summary>
    bool SupportsPaging { get; }

    /// <summary>该图源是否有可配置的标签（决定设置页是否显示标签输入）。</summary>
    bool SupportsTags { get; }

    /// <summary>当前标签串（空格分隔）；仅 <see cref="SupportsTags"/> 为 true 时有意义。</summary>
    string Tags { get; set; }

    /// <summary>Diagnostic message from the most recent failed request, when available.</summary>
    string? LastError { get; }

    /// <summary>Endpoint URL used by the connectivity probe (reachability check).</summary>
    string? ProbeUrl { get; }

    /// <summary>获取一张随机图片；失败或无结果时返回 null。</summary>
    Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>批量获取至多 <paramref name="count"/> 张图片（随机模式）。</summary>
    Task<IReadOnlyList<ImageItem>> GetImagesAsync(
        NsfwMode mode,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定页（1 基）的图片，供分页浏览。</summary>
    Task<IReadOnlyList<ImageItem>> GetImagesPageAsync(
        NsfwMode mode,
        int page,
        int perPage,
        CancellationToken cancellationToken = default);
}
