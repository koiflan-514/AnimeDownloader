namespace AnimeDownloader.Core.Services;

/// <summary>
/// Resolves the thumbnail URL used for grid cards, header previews and viewer toolbars.
/// Priority: native source thumbnail → optional user-provided resize proxy template →
/// original URL (display-only; the byte cache keeps repeat loads free and decoding is bounded).
/// </summary>
public static class ThumbnailResolver
{
    public const string UrlPlaceholder = "{url}";

    /// <summary>
    /// Returns the lightest practical thumbnail URL for an item.
    /// </summary>
    /// <param name="thumbnailUrl">Native thumbnail URL from the source, when available.</param>
    /// <param name="originalUrl">Full-size image URL.</param>
    /// <param name="proxyTemplate">
    /// Optional proxy template containing <see cref="UrlPlaceholder"/>, e.g.
    /// "https://images.weserv.nl/?url={url}&amp;w=480&amp;h=480&amp;fit=cover&amp;output=jpg".
    /// </param>
    public static string? ResolveThumbnailUrl(
        string? thumbnailUrl,
        string? originalUrl,
        string? proxyTemplate = null)
    {
        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return thumbnailUrl;
        }

        if (string.IsNullOrWhiteSpace(originalUrl) ||
            originalUrl.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(proxyTemplate))
        {
            var proxy = ApplyProxyTemplate(proxyTemplate, originalUrl);
            if (proxy is not null)
            {
                return proxy;
            }
        }

        return originalUrl;
    }

    /// <summary>
    /// Replaces <see cref="UrlPlaceholder"/> in <paramref name="template"/> with the
    /// URL-escaped original URL. Returns null when the placeholder is missing.
    /// </summary>
    public static string? ApplyProxyTemplate(string template, string originalUrl)
    {
        if (!template.Contains(UrlPlaceholder, StringComparison.Ordinal))
        {
            return null;
        }

        return template.Replace(UrlPlaceholder, Uri.EscapeDataString(originalUrl), StringComparison.Ordinal);
    }
}
