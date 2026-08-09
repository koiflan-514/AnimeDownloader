namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Konachan source: https://konachan.com (Moebooru family; tags, NSFW rating filter, paging).
/// </summary>
public sealed class KonachanSource : MoebooruSourceBase
{
    public KonachanSource(HttpClient http)
        : base(http, "konachan", "https://konachan.com/post.json", "Konachan",
            "Random images from konachan.com with custom tags.")
    {
    }

    /// <inheritdoc />
    protected override string? BuildPostLink(string id) => $"https://konachan.com/post/show/{id}";
}
