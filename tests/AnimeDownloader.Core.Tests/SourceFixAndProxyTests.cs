using System.Net;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.Core.Tests;

/// <summary>
/// 图源修复与代理基础设施的纯逻辑测试：本地 Stub 应答，不访问真实网络。
/// 覆盖：waifu.im 空 artists 数组、dmoe 百度代理链解码、Gelbooru API 凭据、
/// SOCKS 方案识别与 no_proxy 旁路解析。
/// </summary>
public class SourceFixAndProxyTests
{
    private static HttpClient CreateHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeDownloaderTest/1.0");
        return client;
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    // ---------------- waifu.im：artists 空数组不得崩溃 ----------------

    [Fact]
    public async Task WaifuIm_EmptyArtistsArray_ParsesWithoutCrash()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"items":[{"url":"https://img.waifu.im/a.jpg","id":7,"artists":[],"width":800,"height":600}]}"""));
        var source = new WaifuImSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal("https://img.waifu.im/a.jpg", item.Url);
        Assert.Null(item.Artist);
    }

    [Fact]
    public async Task WaifuIm_ArtistPresent_IsRead()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"items":[{"url":"https://img.waifu.im/b.jpg","id":8,"artists":[{"name":"Alice"}]}]}"""));
        var source = new WaifuImSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal("Alice", item.Artist);
    }

    // ---------------- dmoe：百度图片代理链解码 ----------------

    [Theory]
    [InlineData(
        "https://image.baidu.com/search/down?url=https%3A%2F%2Ftvax3.sinaimg.cn%2Flarge%2Fabc.jpg",
        "https://tvax3.sinaimg.cn/large/abc.jpg")]
    [InlineData("https://www.dmoe.cc/img.png", null)]
    [InlineData("https://image.baidu.com/search/down?url=", null)]
    [InlineData(null, null)]
    public void Dmoe_TryDecodeBaiduProxyUrl_DecodesEmbeddedUrl(string? input, string? expected)
    {
        Assert.Equal(expected, DmoeSource.TryDecodeBaiduProxyUrl(input));
    }

    [Fact]
    public async Task Dmoe_BaiduProxyImgurl_ItemUsesDecodedUrl()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"code":200,"imgurl":"https://image.baidu.com/search/down?url=https%3A%2F%2Ftvax4.sinaimg.cn%2Flarge%2Fx1.jpg"}"""));
        var source = new DmoeSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.ShowEverything);

        Assert.NotNull(item);
        Assert.Equal("https://tvax4.sinaimg.cn/large/x1.jpg", item.Url);
        Assert.Equal("jpg", item.Extension);
    }

    // ---------------- Gelbooru：API 凭据 ----------------

    [Fact]
    public async Task Gelbooru_WithCredentials_QueryContainsUserIdAndApiKey()
    {
        string? requestUrl = null;
        using var http = CreateHttp(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return JsonResponse("""{"@attributes":{},"post":[]}""");
        });
        var source = new GelbooruSource(http)
        {
            UserId = "1234567",
            ApiKey = "secret-key",
        };

        await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        Assert.NotNull(requestUrl);
        Assert.Contains("user_id=1234567", requestUrl);
        Assert.Contains("api_key=secret-key", requestUrl);
    }

    [Fact]
    public async Task Gelbooru_UnauthorizedWithoutCredentials_ShowsFriendlyError()
    {
        using var http = CreateHttp(_ => JsonResponse("{}", HttpStatusCode.Unauthorized));
        var source = new GelbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        Assert.Empty(items);
        Assert.NotNull(source.LastError);
        Assert.Contains("user_id", source.LastError, StringComparison.Ordinal);
        Assert.Contains("api_key", source.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Gelbooru_ProbeUrl_IncludesApiParameters()
    {
        using var http = CreateHttp(_ => JsonResponse("{}"));
        var source = new GelbooruSource(http) { UserId = "42", ApiKey = "k" };

        var probeUrl = source.ProbeUrl;

        Assert.NotNull(probeUrl);
        Assert.Contains("page=dapi", probeUrl);
        Assert.Contains("user_id=42", probeUrl);
        Assert.Contains("api_key=k", probeUrl);
    }

    // ---------------- 代理方案与 no_proxy 旁路 ----------------

    [Theory]
    [InlineData("socks", true)]
    [InlineData("socks5", true)]
    [InlineData("socks5h", true)]
    [InlineData("http", false)]
    [InlineData("https", false)]
    public void HttpClientFactory_IsSocksScheme_ClassifiesSchemes(string scheme, bool expected)
    {
        Assert.Equal(expected, HttpClientFactory.IsSocksScheme(scheme));
    }

    [Fact]
    public void ProxyBypassList_MatchesExactAndSuffixEntries()
    {
        var bypass = ProxyBypassList.Parse("localhost, .internal.example.com, *.corp");

        Assert.NotNull(bypass);
        Assert.True(bypass.IsBypassed("localhost"));
        Assert.True(bypass.IsBypassed("api.internal.example.com"));
        Assert.True(bypass.IsBypassed("internal.example.com"));
        Assert.True(bypass.IsBypassed("git.corp"));
        Assert.False(bypass.IsBypassed("example.com"));
        Assert.False(bypass.IsBypassed("notcorp"));
    }

    [Fact]
    public void ProxyBypassList_Empty_ReturnsNull()
    {
        Assert.Null(ProxyBypassList.Parse(null));
        Assert.Null(ProxyBypassList.Parse(string.Empty));
        Assert.Null(ProxyBypassList.Parse(" , ,"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
