using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Shared adapter for Moebooru-family sites (yande.re, konachan, ...) which expose the same
/// <c>/post.json</c> API with tag/rating filters, paging and preview thumbnails.
/// </summary>
public abstract class MoebooruSourceBase : ImageSourceBase
{
    private const int RandomRetries = 3;
    private const int RandomMaxPage = 10000;
    private const int TaggedRandomMaxPage = 200;
    private const int RandomFallbackPage = 1;
    private const int RandomBatchSize = 100;
    private readonly string _endpoint;
    private readonly string _displayName;
    private readonly string _description;

    protected MoebooruSourceBase(
        HttpClient http,
        string sourceId,
        string endpoint,
        string displayName,
        string description)
        : base(http, sourceId)
    {
        _endpoint = endpoint;
        _displayName = displayName;
        _description = description;
    }

    /// <inheritdoc />
    public override string DisplayName => _displayName;

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTags => true;

    /// <inheritdoc />
    public override string? ProbeUrl => _endpoint;

    /// <summary>Builds the canonical post URL for a post id, or null when the site has none.</summary>
    protected virtual string? BuildPostLink(string id) => null;

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
        for (var attempt = 0; attempt < RandomRetries; attempt++)
        {
            var page = NextRandomPage();
            var data = await GetJsonAsync(
                _endpoint,
                BuildQuery(mode, RandomBatchSize, page),
                cancellationToken)
                .ConfigureAwait(false);
            var posts = data?.AsArray();
            if (posts is null || posts.Count == 0)
            {
                continue;
            }

            return BuildItem(posts[Random.Shared.Next(posts.Count)], data);
        }

        // Tagged searches can have few pages; fall back to the newest page so a valid
        // keyword never ends up as "no results" just because a random page was empty.
        var fallback = await GetJsonAsync(
            _endpoint,
            BuildQuery(mode, RandomBatchSize, RandomFallbackPage),
            cancellationToken).ConfigureAwait(false);
        var fallbackPosts = fallback?.AsArray();
        if (fallbackPosts is null || fallbackPosts.Count == 0)
        {
            return null;
        }

        return BuildItem(fallbackPosts[Random.Shared.Next(fallbackPosts.Count)], fallback);
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

        for (var attempt = 0; attempt < RandomRetries; attempt++)
        {
            var page = NextRandomPage();
            var data = await GetJsonAsync(_endpoint, BuildQuery(mode, count, page), cancellationToken)
                .ConfigureAwait(false);
            var items = CollectItems(data, count);
            if (items.Count > 0)
            {
                return items;
            }
        }

        var fallbackData = await GetJsonAsync(
            _endpoint,
            BuildQuery(mode, count, RandomFallbackPage),
            cancellationToken).ConfigureAwait(false);
        return CollectItems(fallbackData, count);
    }

    /// <summary>
    /// With tags, random pages are bounded so keyword searches rarely hit empty pages;
    /// without tags, a wide range keeps results varied.
    /// </summary>
    private int NextRandomPage() =>
        string.IsNullOrWhiteSpace(Tags)
            ? Random.Shared.Next(1, RandomMaxPage + 1)
            : Random.Shared.Next(1, TaggedRandomMaxPage + 1);

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

        var data = await GetJsonAsync(
            _endpoint,
            BuildQuery(mode, perPage, Math.Max(page, 1)),
            cancellationToken).ConfigureAwait(false);
        return CollectItems(data, perPage);
    }

    private List<ImageItem> CollectItems(JsonNode? data, int max)
    {
        var posts = data?.AsArray();
        if (posts is null)
        {
            return new List<ImageItem>();
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

    private ImageItem? BuildItem(JsonNode? post, JsonNode? root)
    {
        var url = post?["file_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var id = post?["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var author = post?["author"]?.GetValue<string>();
        var extension = InferExtension(url);
        var thumbnail = post?["preview_url"]?.GetValue<string>();
        var source = post?["source"]?.GetValue<string>();
        var width = post?["width"]?.GetValue<long>();
        var height = post?["height"]?.GetValue<long>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: author,
            SourceLink: id is null
                ? null
                : string.IsNullOrWhiteSpace(source) ? BuildPostLink(id) : source,
            Id: id,
            Extension: extension,
            Metadata: BuildMetadata(root, width, height));
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