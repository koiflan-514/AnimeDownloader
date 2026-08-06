using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Safebooru 图源：https://safebooru.org（仅全年龄内容，支持标签与分页；
/// 分页参数 pid 为 0 基，对应参考项目实现）。
/// </summary>
public sealed class SafebooruSource : ImageSourceBase
{
    private const string Endpoint = "https://safebooru.org/index.php";
    private const int RandomFetchLimit = 100;

    public SafebooruSource(HttpClient http)
        : base(http, "safebooru")
    {
    }

    public override string DisplayName => "Safebooru";

    public override string Description => "Random images from safebooru.org (safe-for-work only).";

    public override bool SupportsPaging => true;

    public override bool SupportsTags => true;

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
        var data = await GetJsonAsync(Endpoint, BaseQuery(RandomFetchLimit, null), cancellationToken).ConfigureAwait(false);
        var posts = data?.AsArray();
        if (posts is null || posts.Count == 0)
        {
            return null;
        }

        var index = Random.Shared.Next(posts.Count);
        return BuildItem(posts[index], data);
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

        var data = await GetJsonAsync(Endpoint, BaseQuery(Math.Max(count, 1), 0), cancellationToken).ConfigureAwait(false);
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

        return new ImageItem(
            Url: url,
            Artist: owner,
            SourceLink: id is null ? null : $"https://safebooru.org/index.php?page=post&s=view&id={id}",
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
