using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Gelbooru source: https://gelbooru.com (Gelbooru API v1; tags, NSFW rating filter, paging).
/// The JSON response shape is {"@attributes": {...}, "post": [...]}.
/// </summary>
public sealed class GelbooruSource : ImageSourceBase
{
    private const string Endpoint = "https://gelbooru.com/index.php";
    private const int RandomFetchLimit = 100;
    private const int TaggedRandomMaxPid = 200;

    public GelbooruSource(HttpClient http)
        : base(http, "gelbooru")
    {
    }

    public override string DisplayName => "Gelbooru";

    public override string Description => "Random images from gelbooru.com with custom tags.";

    public override bool SupportsPaging => true;

    public override bool SupportsTags => true;

    /// <inheritdoc />
    public override string? ProbeUrl => Endpoint;

    private Dictionary<string, string?> BaseQuery(int? limit, int? pid, NsfwMode mode)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = "dapi",
            ["s"] = "post",
            ["q"] = "index",
            ["json"] = "1",
        };
        if (limit is not null)
        {
            query["limit"] = limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (pid is not null)
        {
            query["pid"] = pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var rating = mode switch
        {
            NsfwMode.BlockNsfw => "rating:general",
            NsfwMode.OnlyNsfw => "rating:explicit",
            _ => null,
        };
        var tags = Tags.Trim();
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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = await GetJsonAsync(
                Endpoint,
                BaseQuery(RandomFetchLimit, NextRandomPid(), mode),
                cancellationToken).ConfigureAwait(false);
            var posts = ReadPosts(data);
            if (posts.Count > 0)
            {
                return BuildItem(posts[Random.Shared.Next(posts.Count)], data);
            }
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BaseQuery(RandomFetchLimit, 0, mode),
            cancellationToken).ConfigureAwait(false);
        var fallbackPosts = ReadPosts(fallback);
        return fallbackPosts.Count == 0 ? null : BuildItem(fallbackPosts[0], fallback);
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = await GetJsonAsync(
                Endpoint,
                BaseQuery(Math.Max(count, 1), NextRandomPid(), mode),
                cancellationToken).ConfigureAwait(false);
            var items = CollectItems(data, count);
            if (items.Count > 0)
            {
                return items;
            }
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BaseQuery(Math.Max(count, 1), 0, mode),
            cancellationToken).ConfigureAwait(false);
        return CollectItems(fallback, count);
    }

    /// <summary>Tagged searches stay near the front pages so keyword results are non-empty.</summary>
    private int NextRandomPid() =>
        string.IsNullOrWhiteSpace(Tags)
            ? Random.Shared.Next(0, 5001)
            : Random.Shared.Next(0, TaggedRandomMaxPid + 1);

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

        var pid = Math.Max(page - 1, 0);
        var data = await GetJsonAsync(Endpoint, BaseQuery(perPage, pid, mode), cancellationToken)
            .ConfigureAwait(false);
        return CollectItems(data, perPage);
    }

    private static List<JsonNode> ReadPosts(JsonNode? data)
    {
        var posts = data?["post"]?.AsArray();
        if (posts is null)
        {
            return new List<JsonNode>();
        }

        return posts.Where(p => p is not null).Cast<JsonNode>().ToList();
    }

    private static List<ImageItem> CollectItems(JsonNode? data, int max)
    {
        var posts = ReadPosts(data);
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

        var id = ReadStringValue(post?["id"]);
        var owner = post?["owner"]?.GetValue<string>();
        var extension = InferExtension(url);
        var thumbnail = post?["preview_url"]?.GetValue<string>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: owner,
            SourceLink: id is null ? null : $"https://gelbooru.com/index.php?page=post&s=view&id={id}",
            Id: id,
            Extension: extension,
            Metadata: new Dictionary<string, object?> { ["raw"] = root?.ToJsonString() });
    }

    private static string? ReadStringValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node?.ToString(),
    };

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
