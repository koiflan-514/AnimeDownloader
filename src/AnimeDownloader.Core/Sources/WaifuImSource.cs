using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Waifu 图源：https://api.waifu.im（支持分页，IsNsfw 参数大小写敏感）。
/// </summary>
public sealed class WaifuImSource : ImageSourceBase
{
    private const string Endpoint = "https://api.waifu.im/images";

    public WaifuImSource(HttpClient http)
        : base(http, "waifuim")
    {
    }

    public override string DisplayName => "Waifu";

    public override string Description => "Generate images from waifu.im.";

    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override string? ProbeUrl => Endpoint;

    private static string NsfwParameter(NsfwMode mode) => mode switch
    {
        NsfwMode.ShowEverything => "All",
        NsfwMode.OnlyNsfw => "True",
        _ => "False",
    };

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = await GetJsonAsync(
                Endpoint,
                BuildQuery(mode, 1, Random.Shared.Next(1, 2001)),
                cancellationToken).ConfigureAwait(false);
            var item = data?["items"]?[0];
            if (item is not null)
            {
                return BuildItem(item, data);
            }
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BuildQuery(mode, 1, 1),
            cancellationToken).ConfigureAwait(false);
        var fallbackItem = fallback?["items"]?[0];
        return fallbackItem is null ? null : BuildItem(fallbackItem, fallback);
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
                BuildQuery(mode, Math.Min(count, 30), Random.Shared.Next(1, 2001)),
                cancellationToken).ConfigureAwait(false);
            var items = CollectItems(data, count);
            if (items.Count > 0)
            {
                return items;
            }
        }

        var fallback = await GetJsonAsync(
            Endpoint,
            BuildQuery(mode, Math.Min(count, 30), 1),
            cancellationToken).ConfigureAwait(false);
        return CollectItems(fallback, count);
    }

    private static Dictionary<string, string?> BuildQuery(NsfwMode mode, int pageSize, int page)
    {
        return new Dictionary<string, string?>
        {
            ["IsNsfw"] = NsfwParameter(mode),
            ["PageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static List<ImageItem> CollectItems(JsonNode? data, int max)
    {
        var items = new List<ImageItem>(max);
        foreach (var node in data?["items"]?.AsArray() ?? Enumerable.Empty<JsonNode?>())
        {
            var item = BuildItem(node, data);
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

        var pageSize = Math.Min(perPage, 30);
        var data = await GetJsonAsync(
            Endpoint,
            new Dictionary<string, string?>
            {
                ["IsNsfw"] = NsfwParameter(mode),
                ["PageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Page"] = Math.Max(page, 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        var items = new List<ImageItem>(perPage);
        foreach (var node in data?["items"]?.AsArray() ?? Enumerable.Empty<JsonNode?>())
        {
            var item = BuildItem(node, data);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static ImageItem? BuildItem(JsonNode? node, JsonNode? root)
    {
        var url = node?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var id = ReadStringValue(node?["id"]);
        var artist = ReadStringValue(node?["artists"]?[0]?["name"]);
        var source = ReadStringValue(node?["source"]);
        var extension = ReadStringValue(node?["extension"]) ?? InferExtension(url);
        var width = node?["width"]?.GetValue<long>();
        var height = node?["height"]?.GetValue<long>();

        return new ImageItem(
            Url: url,
            Artist: artist,
            SourceLink: source,
            Id: id,
            Extension: extension,
            Metadata: BuildMetadata(root, width, height));
    }

    /// <summary>将 JSON 节点读取为字符串：兼容字符串与数字（如 waifu.im 的 id 为数字）。</summary>
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