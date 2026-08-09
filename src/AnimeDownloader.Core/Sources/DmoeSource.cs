using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// 樱花随机图：https://www.dmoe.cc（国内可直连，无标签、无分页、无元数据）。
/// </summary>
public sealed class DmoeSource : ImageSourceBase
{
    private const string Endpoint = "https://www.dmoe.cc/random.php";

    public DmoeSource(HttpClient http)
        : base(http, "dmoe")
    {
    }

    public override string DisplayName => "Sakura Random";

    public override string Description => "Random anime images from dmoe.cc (accessible in China).";

    /// <inheritdoc />
    public override string? ProbeUrl => Endpoint;

    /// <inheritdoc />
    public override async Task<ImageItem?> GetRandomImageAsync(
        NsfwMode mode,
        CancellationToken cancellationToken = default)
    {
        var data = await GetJsonAsync(Endpoint, new Dictionary<string, string?> { ["return"] = "json" }, cancellationToken)
            .ConfigureAwait(false);
        var url = data?["imgurl"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return new ImageItem(
            Url: url,
            Id: DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Extension: InferExtension(url));
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

        // dmoe 无批量接口，只能逐张拉取：并行发起请求以显著提升画廊加载速度。
        var attempts = Math.Min(count * 2, 20);
        var tasks = new List<Task<ImageItem?>>(attempts);
        for (var i = 0; i < attempts; i++)
        {
            tasks.Add(GetRandomImageAsync(mode, cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var items = new List<ImageItem>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in results)
        {
            if (item is null)
            {
                continue;
            }

            if (seen.Add(item.Url))
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
