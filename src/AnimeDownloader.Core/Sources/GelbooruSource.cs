using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Gelbooru source: https://gelbooru.com (Gelbooru API v1; tags, NSFW rating filter, paging).
/// The JSON response shape is {"@attributes": {...}, "post": [...]}. 支持标签联想（dapi tag）。
/// </summary>
public sealed class GelbooruSource : ImageSourceBase, ITagSuggester
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

    /// <summary>
    /// Gelbooru 账号 user_id（2022-09 起 API 强制要求与 api_key 成对提供，见设置页）。
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>Gelbooru 账号 api_key，与 <see cref="UserId"/> 成对使用。</summary>
    public string? ApiKey { get; set; }

    private bool HasCredentials =>
        !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>API 请求地址（带凭据时附上 user_id 与 api_key，供连通性探测与真实请求共用）。</summary>
    private string ApiUrl =>
        HasCredentials
            ? $"{Endpoint}?user_id={Uri.EscapeDataString(UserId!.Trim())}&api_key={Uri.EscapeDataString(ApiKey!.Trim())}"
            : Endpoint;

    /// <inheritdoc />
    public override string? ProbeUrl => $"{ApiUrl}?page=dapi&s=post&q=index&json=1&limit=1";

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

    /// <summary>调用 API 并把 401 翻译为可操作的提示（匿名访问已被 Gelbooru 停用）。</summary>
    private async Task<JsonNode?> GetApiJsonAsync(
        Dictionary<string, string?> query,
        CancellationToken cancellationToken)
    {
        var node = await GetJsonAsync(ApiUrl, query, cancellationToken).ConfigureAwait(false);
        if (node is null && LastError is not null && LastError.Contains("401", StringComparison.Ordinal))
        {
            LastError = "Gelbooru 已停用匿名 API：请在设置页填写 user_id 与 api_key 后重试";
        }

        return node;
    }

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = await GetApiJsonAsync(
                BaseQuery(RandomFetchLimit, NextRandomPid(), mode),
                cancellationToken).ConfigureAwait(false);
            var posts = ReadPosts(data);
            if (posts.Count > 0)
            {
                return BuildItem(posts[Random.Shared.Next(posts.Count)], data);
            }
        }

        var fallback = await GetApiJsonAsync(
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
            var data = await GetApiJsonAsync(
                BaseQuery(Math.Max(count, 1), NextRandomPid(), mode),
                cancellationToken).ConfigureAwait(false);
            var items = CollectItems(data, count);
            if (items.Count > 0)
            {
                return items;
            }
        }

        var fallback = await GetApiJsonAsync(
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
        var data = await GetApiJsonAsync(BaseQuery(perPage, pid, mode), cancellationToken)
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
        var width = post?["width"]?.GetValue<long>();
        var height = post?["height"]?.GetValue<long>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: owner,
            SourceLink: id is null ? null : $"https://gelbooru.com/index.php?page=post&s=view&id={id}",
            Id: id,
            Extension: extension,
            Metadata: BuildMetadata(root, width, height),
            Tags: ReadTags(post));
    }

    /// <summary>读取帖子全部标签（tags 为空格分隔字符串）。</summary>
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

    // ---------------- 标签联想（ITagSuggester，dapi tag 查询需要凭据） ----------------

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

        var suggestions = new List<TagSuggestion>();
        if (HasCredentials)
        {
            var data = await GetJsonAsync(ApiUrl, new Dictionary<string, string?>
            {
                ["page"] = "dapi",
                ["s"] = "tag",
                ["q"] = "index",
                ["name_pattern"] = $"{prefix}*",
                ["orderby"] = "count",
                ["limit"] = Math.Clamp(limit, 1, 30).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["json"] = "1",
            }, cancellationToken).ConfigureAwait(false);
            var nodes = data?["tag"]?.AsArray();
            if (nodes is null)
            {
                return TagSuggesterFallback.LocalPrefix(prefix, limit);
            }

            foreach (var node in nodes)
            {
                var name = node?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var count = node?["count"]?.GetValue<long>();
                // Gelbooru tag type：0=general 1=artist 2=copyright 3=character 4=meta
                var typeValue = node?["type"]?.GetValue<int>();
                TagCategory? category = typeValue switch
                {
                    1 => TagCategory.Artist,
                    2 => TagCategory.Copyright,
                    3 => TagCategory.Character,
                    4 => TagCategory.Meta,
                    0 => TagCategory.General,
                    _ => null,
                };
                TagLocalization.TryGet(name, out var localized);
                suggestions.Add(new TagSuggestion(name, count, category, localized.ChineseName));
            }
        }

        if (suggestions.Count == 0)
        {
            // 无凭据或在线联想为空时回退到本地热门标签表
            return TagSuggesterFallback.LocalPrefix(prefix, limit);
        }

        return suggestions;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TagSuggestion>> GetRelatedTagsAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TagSuggestion>>(Array.Empty<TagSuggestion>());
    }
}