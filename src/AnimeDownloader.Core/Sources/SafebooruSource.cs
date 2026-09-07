using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Safebooru 图源：https://safebooru.org（仅全年龄内容，支持标签与分页；
/// 分页参数 pid 为 0 基，对应参考项目实现）。支持标签联想（dapi tag 查询）。
/// </summary>
public sealed class SafebooruSource : ImageSourceBase, ITagSuggester
{
    private const string Endpoint = "https://safebooru.org/index.php";
    private const int RandomFetchLimit = 100;
    private const int TaggedRandomMaxPid = 200;

    public SafebooruSource(HttpClient http)
        : base(http, "safebooru")
    {
    }

    public override string DisplayName => "Safebooru";

    public override string Description => "Random images from safebooru.org (safe-for-work only).";

    public override bool SupportsPaging => true;

    public override bool SupportsTags => true;

    /// <inheritdoc />
    public override string? ProbeUrl => Endpoint;

    private Dictionary<string, string?> BaseQuery(int? limit, int? pid)
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

        var tags = Tags.Trim();
        if (tags.Length > 0)
        {
            query["tags"] = tags;
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
                BaseQuery(RandomFetchLimit, NextRandomPid()),
                cancellationToken).ConfigureAwait(false);
            var posts = data?.AsArray();
            if (posts is null || posts.Count == 0)
            {
                continue;
            }

            return BuildItem(posts[Random.Shared.Next(posts.Count)], data);
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BaseQuery(RandomFetchLimit, 0),
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = await GetJsonAsync(
                Endpoint,
                BaseQuery(Math.Max(count, 1), NextRandomPid()),
                cancellationToken).ConfigureAwait(false);
            var items = CollectItems(data, count);
            if (items.Count > 0)
            {
                return items;
            }
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BaseQuery(Math.Max(count, 1), 0),
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
        var data = await GetJsonAsync(Endpoint, BaseQuery(perPage, pid), cancellationToken).ConfigureAwait(false);
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
        var owner = post?["owner"]?.GetValue<string>();
        var extension = InferExtension(url);
        var thumbnail = post?["preview_url"]?.GetValue<string>();
        var width = post?["width"]?.GetValue<long>();
        var height = post?["height"]?.GetValue<long>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: owner,
            SourceLink: id is null ? null : $"https://safebooru.org/index.php?page=post&s=view&id={id}",
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

    // ---------------- 标签联想（ITagSuggester） ----------------

    /// <inheritdoc />
    public bool SupportsTagSuggestions => true;

    /// <summary>
    /// Safebooru 的 dapi tag 查询不支持通配符前缀且 json=1 输出损坏（恒为空），
    /// 因此联想直接使用内嵌热门标签表的本地前缀匹配（含中文译名与近似热度）。
    /// </summary>
    public Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        string input,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        var prefix = input.Trim();
        if (prefix.Length == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<TagSuggestion>>(Array.Empty<TagSuggestion>());
        }

        var matches = TagLocalization.SearchByPrefix(prefix, Math.Clamp(limit, 1, 30));
        var suggestions = new List<TagSuggestion>(matches.Count);
        foreach (var match in matches)
        {
            suggestions.Add(new TagSuggestion(
                match.Name,
                match.Entry.PostCount,
                ParseCategory(match.Entry.Category),
                match.Entry.ChineseName));
        }

        return Task.FromResult<IReadOnlyList<TagSuggestion>>(suggestions);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TagSuggestion>> GetRelatedTagsAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TagSuggestion>>(Array.Empty<TagSuggestion>());
    }

    private static TagCategory? ParseCategory(int value) => value switch
    {
        1 => TagCategory.Artist,
        2 => TagCategory.Copyright,
        3 => TagCategory.Character,
        4 => TagCategory.Meta,
        0 => TagCategory.General,
        _ => null,
    };
}