using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.Core.Services;

/// <summary>Per-source connectivity result.</summary>
/// <param name="SourceId">Source id (e.g. "danbooru").</param>
/// <param name="DisplayName">User-facing source name.</param>
/// <param name="ProbeUrl">Endpoint that was probed.</param>
/// <param name="DirectReachable">True when the endpoint answered without a proxy.</param>
/// <param name="ProxyReachable">True when the endpoint answered through the configured proxy.</param>
/// <param name="DirectDetail">HTTP status or error for the direct attempt.</param>
/// <param name="ProxyDetail">HTTP status or error for the proxied attempt.</param>
public sealed record SourceProbeResult(
    string SourceId,
    string DisplayName,
    string? ProbeUrl,
    bool DirectReachable,
    bool ProxyReachable,
    string DirectDetail,
    string ProxyDetail)
{
    public bool NeedsProxy => !DirectReachable && ProxyReachable;

    public bool Unreachable => !DirectReachable && !ProxyReachable;

    public bool Direct => DirectReachable;
}

/// <summary>
/// Probes every source endpoint twice — once with a direct client and once with a proxied
/// client — so the UI can classify each source as "direct", "needs proxy" or "unreachable".
/// </summary>
public static class ConnectivityProbe
{
    public static async Task<IReadOnlyList<SourceProbeResult>> ProbeAsync(
        IReadOnlyList<IImageSource> sources,
        HttpClient directHttp,
        HttpClient proxiedHttp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(directHttp);
        ArgumentNullException.ThrowIfNull(proxiedHttp);

        var results = new List<SourceProbeResult>(sources.Count);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.ProbeUrl))
            {
                continue;
            }

            var direct = await TryProbeAsync(directHttp, source.ProbeUrl, cancellationToken).ConfigureAwait(false);
            var proxied = await TryProbeAsync(proxiedHttp, source.ProbeUrl, cancellationToken).ConfigureAwait(false);
            results.Add(new SourceProbeResult(
                source.Id,
                source.DisplayName,
                source.ProbeUrl,
                direct.reachable,
                proxied.reachable,
                direct.detail,
                proxied.detail));
        }

        return results;
    }

    private static async Task<(bool reachable, string detail)> TryProbeAsync(
        HttpClient http,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return (true, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "超时");
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return (false, message.Length > 80 ? message[..80] : message);
        }
    }
}
