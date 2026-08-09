using System.Net;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.Core.Tests;

public class ConnectivityProbeTests
{
    [Fact]
    public async Task ProbeAsync_ClassifiesDirectProxyAndUnreachable()
    {
        var sources = new IImageSource[]
        {
            new NekosMoeSource(CreateHttp(_ => Ok())),
            new YandeReSource(CreateHttp(_ => Ok())),
        };

        using var direct = new HttpClient(new StubHandler(_ => Ok()));
        using var proxied = new HttpClient(new StubHandler(_ => throw new HttpRequestException("boom")));
        var results = await ConnectivityProbe.ProbeAsync(sources, direct, proxied);

        var directResult = Assert.Single(results, r => r.SourceId == "nekosmoe");
        Assert.True(directResult.Direct);
        Assert.True(directResult.NeedsProxy == false);
    }

    [Fact]
    public async Task ProbeAsync_DirectFailsProxyOk_IsNeedsProxy()
    {
        var source = new YandeReSource(CreateHttp(_ => Ok()));
        using var direct = new HttpClient(new StubHandler(_ => throw new HttpRequestException("timeout")));
        using var proxied = new HttpClient(new StubHandler(_ => Ok()));

        var results = await ConnectivityProbe.ProbeAsync(new[] { source }, direct, proxied);

        var result = Assert.Single(results);
        Assert.True(result.NeedsProxy);
        Assert.False(result.Direct);
    }

    [Fact]
    public void Sources_ExposeProbeUrls()
    {
        using var http = new HttpClient();
        var registry = SourceRegistry.CreateAll(http);

        Assert.All(registry, s => Assert.False(string.IsNullOrWhiteSpace(s.ProbeUrl)));
    }

    private static HttpClient CreateHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new HttpClient(new StubHandler(responder));
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
