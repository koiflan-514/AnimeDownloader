using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Yande.re 图源：https://yande.re（支持标签、NSFW 分级过滤与分页，
/// 通过 rating 标签实现，对应参考项目实现）。
/// </summary>
public sealed class YandeReSource : ImageSourceBase
{
    private const string Endpoint = "https://yande.re/post.json";

    public YandeReSource(HttpClient http)
        : base(http, "yandere")
    {
    }

    public override string DisplayName => "Yande.re";

    public override string Description => "Random images from yande.re with custom tags.";

    public override bool SupportsPaging => true;

    public override bool SupportsTags => true;

    private Dictionary<string, string?> BuildQuery(NsfwMode mode, int? limit, int? page)
    {
        var query = new Dictionary<string, string?>();
        if (limit is not null)
        {
            query["limit"] = limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (page is not null)
        {
            query["page"] = page.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var tags = Tags.Trim();
        var rating = mode switch
        {
            NsfwMode.BlockNsfw => "rating:safe",
            NsfwMode.OnlyNsfw => "rating:explicit",
            _ => null,
        };

        var combined = tags.Length > 0 && rating is not null ? $"{tags} {rating}" : rating ?? tags;
        if (combined.Length > 0)
        {
            query["tags"] = combined;
        }

        return query;
    }

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        var data = await GetJsonAsync(Endpoint, BuildQuery(mode, 1, null), cancellationToken).ConfigureAwait(false);
        var post = data?.AsArray()?.FirstOrDefault();
        return post is null ? null : BuildItem(post, data);
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

        var data = await GetJsonAsync(Endpoint, BuildQuery(mode, count, null), cancellationToken).ConfigureAwait(false);
        return CollectItems(data, count);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ImageItem>> GetImagesPageAsync(
        NsfwMode mode,
        int page,
        int perPage,
        CancellationToken cancellationToken = default)
    {
        if (perPage <= 0)
        {
            return Array.Empty<ImageItem>();
        }

        var data = await GetJsonAsync(Endpoint, BuildQuery(mode, perPage, Math.Max(page, 1)), cancellationToken)
            .ConfigureAwait(false);
        return CollectItems(data, perPage);
    }

    private static IReadOnlyList<ImageItem> CollectItems(JsonNode? data, int max)
    {
        var posts = data?.AsArray();
        if (posts is null)
        {
            return Array.Empty<ImageItem>();
        }

        var items = new List<ImageItem>(max);
        foreach (var post in posts)
        {
            var item = BuildItem(post, data);
            if (item is not null)
            {
                items.Add(item);
            }

            if (items.Count >= max)
            {
                break;
            }
        }

        return items;
    }

    private static ImageItem? BuildItem(JsonNode? post, JsonNode? root)
    {
        var url = post?["file_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var id = post?["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var author = post?["author"]?.GetValue<string>();
        var extension = InferExtension(url);

        return new ImageItem(
            Url: url,
            Artist: author,
            SourceLink: id is null ? null : $"https://yande.re/post/show/{id}",
            Id: id,
            Extension: extension,
            Metadata: new Dictionary<string, object?> { ["raw"] = root?.ToJsonString() });
    }

    private static string? InferExtension(string url)
    {
        var path = url.Split('?')[0];
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return null;
        }

        var ext = path[(dot + 1)..];
        return ext.Length is > 0 and <= 8 ? ext : null;
    }
}
