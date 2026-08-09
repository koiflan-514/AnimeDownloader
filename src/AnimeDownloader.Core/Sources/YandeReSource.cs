namespace AnimeDownloader.Core.Sources;

/// <summary>
/// Yande.re source: https://yande.re (Moebooru family; tags, NSFW rating filter, paging).
/// </summary>
public sealed class YandeReSource : MoebooruSourceBase
{
    public YandeReSource(HttpClient http)
        : base(http, "yandere", "https://yande.re/post.json", "Yande.re",
            "Random images from yande.re with custom tags.")
    {
    }

    /// <inheritdoc />
    protected override string? BuildPostLink(string id) => $"https://yande.re/post/show/{id}";
}
