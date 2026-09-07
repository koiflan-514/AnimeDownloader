using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Danbooru 图源：https://danbooru.donmai.us（支持标签、随机与分页）。
/// 与参考项目一致，对 Danbooru 平台的受限标签（shota/loli）进行下载端过滤，
/// 并在标签输入时静默剔除。支持标签联想与相关标签推荐。
/// </summary>
public sealed class DanbooruSource : ImageSourceBase, ITagSuggester
{
    private const string Endpoint = "https://danbooru.donmai.us";
    private static readonly string[] ForbiddenTags = { "shota", "loli" };
    private const int MaxRetries = 5;

    public DanbooruSource(HttpClient http)
        : base(http, "danbooru")
    {
    }

    public override string DisplayName => "Danbooru";

    public override string Description => "Generate images from danbooru.donmai.us with custom tags.";

    public override bool SupportsPaging => true;

    public override bool SupportsTags => true;

    /// <inheritdoc />
    public override string? ProbeUrl => $"{Endpoint}/posts.json";

    /// <summary>判断标签串是否含受限标签（用于设置页即时反馈）。</summary>
    public static bool ContainsForbiddenTag(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return false;
        }

        var set = new HashSet<string>(
            tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return ForbiddenTags.Any(set.Contains);
    }

    /// <summary>从标签串中剔除受限标签（保留其余标签）。</summary>
    public static string RemoveForbiddenTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        var set = new HashSet<string>(ForbiddenTags, StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !set.Contains(t)));
    }

    private string BuildTagsQuery(NsfwMode mode)
    {
        var tags = Tags.Trim();
        var rating = mode switch
        {
            NsfwMode.BlockNsfw => "rating:general",
            NsfwMode.OnlyNsfw => "rating:explicit",
            _ => null,
        };

        if (tags.Length > 0 && rating is not null)
        {
            return $"{tags} {rating}";
        }

        return rating ?? tags;
    }

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var query = new Dictionary<string, string?>
            {
                ["limit"] = "1",
                ["random"] = "true",
            };
            var tags = BuildTagsQuery(mode);
            if (tags.Length > 0)
            {
                query["tags"] = tags;
            }

            var data = await GetJsonAsync($"{Endpoint}/posts.json", query, cancellationToken).ConfigureAwait(false);
            var post = data?.AsArray()?.FirstOrDefault();
            if (post is null)
            {
                return null;
            }

            if (HasForbiddenTag(post))
            {
                continue;
            }

            return BuildItem(post, data);
        }

        return null;
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

        var query = new Dictionary<string, string?>
        {
            ["limit"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["random"] = "true",
        };
        var tags = BuildTagsQuery(mode);
        if (tags.Length > 0)
        {
            query["tags"] = tags;
        }

        var data = await GetJsonAsync($"{Endpoint}/posts.json", query, cancellationToken).ConfigureAwait(false);
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

        var query = new Dictionary<string, string?>
        {
            ["limit"] = perPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["page"] = Math.Max(page, 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var tags = BuildTagsQuery(mode);
        if (tags.Length > 0)
        {
            query["tags"] = tags;
        }

        var data = await GetJsonAsync($"{Endpoint}/posts.json", query, cancellationToken).ConfigureAwait(false);
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
            if (HasForbiddenTag(post))
            {
                continue;
            }

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

    private static bool HasForbiddenTag(JsonNode? post)
    {
        var tagString = post?["tag_string"]?.GetValue<string>();
        if (string.IsNullOrEmpty(tagString))
        {
            return false;
        }

        var set = new HashSet<string>(
            tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        return ForbiddenTags.Any(set.Contains);
    }

    private static ImageItem? BuildItem(JsonNode? post, JsonNode? root)
    {
        var url = post?["file_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var id = post?["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var artist = post?["tag_string_artist"]?.GetValue<string>()?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var extension = InferExtension(url);
        var thumbnail = post?["preview_file_url"]?.GetValue<string>();
        var width = post?["image_width"]?.GetValue<long>();
        var height = post?["image_height"]?.GetValue<long>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: artist,
            SourceLink: id is null ? null : $"{Endpoint}/posts/{id}",
            Id: id,
            Extension: extension,
            Metadata: BuildMetadata(root, width, height),
            Tags: ReadTags(post));
    }

    /// <summary>
    /// 读取帖子的全部标签并附带类别：tag_string 为权威顺序，
    /// 各分类串（artist/character/copyright/meta）用于建立 名称→类别 映射。
    /// </summary>
    private static IReadOnlyList<ImageTag> ReadTags(JsonNode? post)
    {
        var tagString = post?["tag_string"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tagString))
        {
            return Array.Empty<ImageTag>();
        }

        var categories = new Dictionary<string, TagCategory>(StringComparer.Ordinal);
        AddCategory(categories, post, "tag_string_artist", TagCategory.Artist);
        AddCategory(categories, post, "tag_string_character", TagCategory.Character);
        AddCategory(categories, post, "tag_string_copyright", TagCategory.Copyright);
        AddCategory(categories, post, "tag_string_meta", TagCategory.Meta);

        var tags = new List<ImageTag>();
        foreach (var name in tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Length == 0)
            {
                continue;
            }

            categories.TryGetValue(name, out var category);
            tags.Add(new ImageTag(name, category));
        }

        return tags;
    }

    private static void AddCategory(
        Dictionary<string, TagCategory> target,
        JsonNode? post,
        string fieldName,
        TagCategory category)
    {
        var value = post?[fieldName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var name in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Length > 0)
            {
                target.TryAdd(name, category);
            }
        }
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

        var data = await GetJsonAsync($"{Endpoint}/tags.json", new Dictionary<string, string?>
        {
            ["search[name_matches]"] = $"{prefix}*",
            ["search[order]"] = "count",
            ["search[is_deprecated]"] = "false",
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

            var count = node?["post_count"]?.GetValue<long>();
            var category = ParseCategory(node?["category"]?.GetValue<int>());
            TagLocalization.TryGet(name, out var localized);
            suggestions.Add(new TagSuggestion(
                name,
                count,
                category,
                localized.ChineseName));
        }

        if (suggestions.Count == 0)
        {
            // 联想接口不可用（网络 / 站点波动）时回退到本地热门标签表
            return TagSuggesterFallback.LocalPrefix(prefix, limit);
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

        var data = await GetJsonAsync($"{Endpoint}/related_tag.json", new Dictionary<string, string?>
        {
            ["search[tag_query]"] = name,
            ["limit"] = "12",
        }, cancellationToken).ConfigureAwait(false);
        var nodes = data?["related_tags"]?.AsArray();
        if (nodes is null)
        {
            return Array.Empty<TagSuggestion>();
        }

        var suggestions = new List<TagSuggestion>(nodes.Count);
        foreach (var node in nodes)
        {
            // 形如 {"tag": {"name": ..., "post_count": ..., "category": ...}, ...}
            var tagNode = node?["tag"] ?? node;
            var relatedName = tagNode?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(relatedName) ||
                relatedName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var count = tagNode?["post_count"]?.GetValue<long>();
            var category = ParseCategory(tagNode?["category"]?.GetValue<int>());
            TagLocalization.TryGet(relatedName, out var localized);
            suggestions.Add(new TagSuggestion(relatedName, count, category, localized.ChineseName));
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