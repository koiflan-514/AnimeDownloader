using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Catgirl 图源：https://nekos.moe（随机图片，支持 nsfw 过滤与批量）。
/// </summary>
public sealed class NekosMoeSource : ImageSourceBase
{
    private const string Endpoint = "https://nekos.moe/api/v1/random/image";

    public NekosMoeSource(HttpClient http)
        : base(http, "nekosmoe")
    {
    }

    public override string DisplayName => "Catgirl";

    public override string Description => "Generate images from nekos.moe.";

    private static string BuildQuery(NsfwMode mode, int? count = null)
    {
        var query = mode switch
        {
            NsfwMode.OnlyNsfw => "nsfw=true",
            NsfwMode.BlockNsfw => "nsfw=false",
            _ => string.Empty,
        };
        if (count is > 0)
        {
            query = string.IsNullOrEmpty(query) ? $"count={count}" : $"{query}&count={count}";
        }

        return query;
    }

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        var url = BuildQuery(mode).Length > 0 ? $"{Endpoint}?{BuildQuery(mode)}" : Endpoint;
        var data = await GetJsonAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = data?["images"]?[0];
        if (image is null)
        {
            return null;
        }

        var id = image["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new ImageItem(
            Url: $"https://nekos.moe/image/{id}",
            Artist: image["artist"]?.GetValue<string>(),
            SourceLink: $"https://nekos.moe/post/{id}",
            Id: id,
            Metadata: new Dictionary<string, object?> { ["raw"] = data?.ToJsonString() });
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ImageItem>> GetImagesAsync(
        NsfwMode mode,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return Array.Empty<ImageItem>();
        }

        var url = $"{Endpoint}?{BuildQuery(mode, count)}";
        var data = await GetJsonAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
        var images = data?["images"]?.AsArray();
        if (images is null)
        {
            return Array.Empty<ImageItem>();
        }

        var items = new List<ImageItem>(count);
        foreach (var node in images)
        {
            var id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            items.Add(new ImageItem(
                Url: $"https://nekos.moe/image/{id}",
                Artist: node?["artist"]?.GetValue<string>(),
                SourceLink: $"https://nekos.moe/post/{id}",
                Id: id));
            if (items.Count >= count)
            {
                break;
            }
        }

        return items;
    }
}
