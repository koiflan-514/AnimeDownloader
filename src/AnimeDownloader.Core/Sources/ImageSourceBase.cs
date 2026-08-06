using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// 图源适配器的公共基础设施：共享 HttpClient、JSON 读取辅助、文件名建议。
/// </summary>
public abstract class ImageSourceBase : IImageSource
{
    protected ImageSourceBase(HttpClient http, string sourceId)
    {
        Http = http;
        Id = sourceId;
    }

    /// <summary>共享 HttpClient。</summary>
    protected HttpClient Http { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public virtual bool SupportsPaging => false;

    /// <inheritdoc />
    public virtual bool SupportsTags => false;

    /// <inheritdoc />
    public string Tags { get; set; } = string.Empty;

    /// <inheritdoc />
    public abstract Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<ImageItem>> GetImagesAsync(
        NsfwMode mode,
        int count,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ImageItem>> GetImagesPageAsync(
        NsfwMode mode,
        int page,
        int perPage,
        CancellationToken cancellationToken = default)
    {
        // 不支持分页的图源回退到随机批量获取。
        return GetImagesAsync(mode, perPage, cancellationToken);
    }

    /// <summary>发送 GET 并返回 JSON 节点；非 2xx 或非 JSON 时返回 null。</summary>
    protected async Task<JsonNode?> GetJsonAsync(
        string url,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = url;
        if (query is { Count: > 0 })
        {
            var queryString = string.Join('&', query.Select(kv =>
            {
                var value = Uri.EscapeDataString(kv.Value ?? string.Empty);
                return $"{Uri.EscapeDataString(kv.Key)}={value}";
            }));
            requestUrl += (requestUrl.Contains('?') ? "&" : "?") + queryString;
        }

        using var response = await Http.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>建议的默认文件名（不含扩展名）：<c>{Id}_{时间戳}</c>。</summary>
    protected string DefaultFileName(DateTimeOffset? stamp = null) =>
        $"{Id}_{(stamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}";

    /// <summary>根据 ID 与扩展名生成建议文件名。</summary>
    protected static string SuggestFileName(string prefix, string id, string? extension) =>
        extension is null ? $"{prefix}_{id}" : $"{prefix}_{id}.{extension}";
}
