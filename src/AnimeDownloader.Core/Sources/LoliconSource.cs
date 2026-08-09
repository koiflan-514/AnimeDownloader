using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Lolicon 图源：https://api.lolicon.app/setu/v2（POST，Pixiv 原图，
/// 国内可直连；支持 R18 过滤与最多 5 个标签的 AND 搜索）。
/// </summary>
public sealed class LoliconSource : ImageSourceBase
{
    private const string Endpoint = "https://api.lolicon.app/setu/v2";
    private const int MaxRetries = 5;

    public LoliconSource(HttpClient http)
        : base(http, "lolicon")
    {
    }

    public override string DisplayName => "Lolicon";

    public override string Description => "Random Pixiv images from lolicon.app (accessible in China).";

    public override bool SupportsTags => true;

    /// <inheritdoc />
    public override string? ProbeUrl => Endpoint;

    private Dictionary<string, object?> BuildRequestBody(NsfwMode mode, int num)
    {
        var r18 = mode switch
        {
            NsfwMode.OnlyNsfw => 1,
            NsfwMode.ShowEverything => 2,
            _ => 0,
        };

        var body = new Dictionary<string, object?>
        {
            ["r18"] = r18,
            ["num"] = num,
            ["size"] = "original",
        };
        var tags = Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .ToArray();
        if (tags.Length > 0)
        {
            body["tag"] = tags;
        }

        return body;
    }

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            using var response = await Http.PostAsJsonAsync(
                Endpoint, BuildRequestBody(mode, 1), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            JsonNode? data;
            try
            {
                data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (data?["error"]?.GetValue<string>() is { Length: > 0 })
            {
                return null;
            }

            var item = data?["data"]?[0];
            if (item is null)
            {
                continue;
            }

            return BuildItem(item, data);
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await Http.PostAsJsonAsync(
                Endpoint, BuildRequestBody(mode, Math.Min(count, 20)), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            JsonNode? data;
            try
            {
                data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (data?["error"]?.GetValue<string>() is { Length: > 0 })
            {
                return Array.Empty<ImageItem>();
            }

            var items = new List<ImageItem>(count);
            foreach (var node in data?["data"]?.AsArray() ?? Enumerable.Empty<JsonNode?>())
            {
                var item = BuildItem(node, data);
                if (item is not null)
                {
                    items.Add(item);
                }

                if (items.Count >= count)
                {
                    break;
                }
            }

            return items;
        }

        return Array.Empty<ImageItem>();
    }

    private static ImageItem? BuildItem(JsonNode? item, JsonNode? root)
    {
        var urls = item?["urls"];
        var url = urls?["original"]?.GetValue<string>() ?? urls?["regular"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var pid = item?["pid"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pageIndex = item?["p"]?.GetValue<int>() ?? 0;
        var id = pid is null ? null : $"{pid}_{pageIndex}";
        var artist = item?["author"]?.GetValue<string>();
        var extension = InferExtension(url);
        // regular 是 Pixiv 缩略图（比 original 小得多），用作画廊缩略图
        var thumbnail = urls?["regular"]?.GetValue<string>();

        return new ImageItem(
            Url: url,
            ThumbnailUrl: string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            Artist: artist,
            SourceLink: pid is null ? null : $"https://www.pixiv.net/artworks/{pid}",
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
