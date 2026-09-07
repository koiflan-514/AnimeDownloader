using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Shared adapter for Moebooru-family sites (yande.re, konachan, ...) which expose the same
/// <c>/post.json</c> API with tag/rating filters, paging and preview thumbnails.
/// 支持标签联想（/tag.json）与相关标签（/tag/related.json）。
/// </summary>
public abstract class MoebooruSourceBase : ImageSourceBase, ITagSuggester
{
    private const int RandomRetries = 3;
    private const int RandomMaxPage = 10000;
    private const int TaggedRandomMaxPage = 200;
    private const int RandomFallbackPage = 1;
    private const int RandomBatchSize = 100;
    private readonly string _endpoint;
    private readonly string _siteBase;
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
        _siteBase = endpoint.EndsWith("/post.json", StringComparison.Ordinal)
            ? endpoint[..^"/post.json".Length]
            : endpoint;
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
            Metadata: BuildMetadata(root, width, height),
            Tags: ReadTags(post));
    }

    /// <summary>读取帖子全部标签（tags 为空格分隔字符串；类别需联想接口按需查询）。</summary>
    private static IReadOnlyList<ImageTag> ReadTags(JsonNode? post)
    {
        var tagString = post?["tags"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tagString))
        {
            return Array.Empty<ImageTag>();
        }

        var tags = new List<ImageTag>();
        foreach (var name in tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Length > 0)
            {
                tags.Add(new ImageTag(name));
            }
        }

        return tags;
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

    // ---------------- 标签联想与相关标签（ITagSuggester） ----------------

    /// <inheritdoc />
    public bool SupportsTagSuggestions => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        string input,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        var prefix = input.Trim();
        if (prefix.Length == 0 || limit <= 0)
        {
            return Array.Empty<TagSuggestion>();
        }

        var data = await GetJsonAsync($"{_siteBase}/tag.json", new Dictionary<string, string?>
        {
            ["name"] = $"{prefix}*",
            ["order"] = "count",
            ["limit"] = Math.Clamp(limit, 1, 30).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, cancellationToken).ConfigureAwait(false);
        var nodes = data?.AsArray();
        if (nodes is null)
        {
            return Array.Empty<TagSuggestion>();
        }

        var suggestions = new List<TagSuggestion>(nodes.Count);
        foreach (var node in nodes)
        {
            var name = node?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var count = node?["count"]?.GetValue<long>();
            var category = ParseCategory(node?["type"]?.GetValue<int>());
            TagLocalization.TryGet(name, out var localized);
            suggestions.Add(new TagSuggestion(name, count, category, localized.ChineseName));
        }

        return suggestions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagSuggestion>> GetRelatedTagsAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        var name = tag.Trim();
        if (name.Length == 0)
        {
            return Array.Empty<TagSuggestion>();
        }

        var data = await GetJsonAsync($"{_siteBase}/tag/related.json", new Dictionary<string, string?>
        {
            ["tags"] = name,
        }, cancellationToken).ConfigureAwait(false);
        var groups = data?["tags"]?.AsObject();
        if (groups is null)
        {
            return Array.Empty<TagSuggestion>();
        }

        var suggestions = new List<TagSuggestion>();
        foreach (var group in groups)
        {
            if (group.Value?.AsArray() is not { } entries)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                // 条目形如 ["vocaloid", 123456, 3]
                if (entry is not JsonArray array || array.Count < 3)
                {
                    continue;
                }

                var relatedName = array[0]?.GetValue<string>();
                if (string.IsNullOrEmpty(relatedName) ||
                    relatedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var count = array[1]?.GetValue<long>();
                var category = ParseCategory(array[2]?.GetValue<int>());
                TagLocalization.TryGet(relatedName, out var localized);
                suggestions.Add(new TagSuggestion(relatedName, count, category, localized.ChineseName));
                if (suggestions.Count >= 12)
                {
                    return suggestions;
                }
            }
        }

        return suggestions;
    }

    private static TagCategory? ParseCategory(int? value) => value switch
    {
        0 => TagCategory.General,
        1 => TagCategory.Artist,
        2 => TagCategory.Circle,
        3 => TagCategory.Copyright,
        4 => TagCategory.Character,
        5 => TagCategory.Meta,
        _ => null,
    };
}