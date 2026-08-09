using System.Text.Json.Nodes;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.Core.Tests;

/// <summary>
/// 图源适配器的纯逻辑测试：使用本地回环 HttpMessageHandler 返回预置 JSON，
/// 不访问任何真实网络。
/// </summary>
public class SourceLogicTests
{
    private static HttpClient CreateHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeDownloaderTest/1.0");
        return client;
    }

    [Fact]
    public async Task NekosMoe_GetRandomImage_UsesIdToBuildUrl()
    {
        using var http = CreateHttp(_ => JsonResponse("""{"images":[{"id":"abc123","artist":"Test Artist"}]}"""));
        var source = new NekosMoeSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.NotNull(item);
        Assert.Equal("https://nekos.moe/image/abc123", item.Url);
        Assert.Equal("Test Artist", item.Artist);
        Assert.Equal("https://nekos.moe/post/abc123", item.SourceLink);
        Assert.Equal("abc123", item.Id);
        Assert.Null(item.ThumbnailUrl);
    }

    [Fact]
    public async Task NekosMoe_BlockNsfw_AddsNsfwFalseQuery()
    {
        string? requestUrl = null;
        using var http = CreateHttp(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return JsonResponse("""{"images":[{"id":"x"}]}""");
        });
        var source = new NekosMoeSource(http);

        await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Contains("nsfw=false", requestUrl);
    }

    [Fact]
    public async Task NekosMoe_GetImages_RespectsCount()
    {
        using var http = CreateHttp(_ => JsonResponse("""{"images":[{"id":"1"},{"id":"2"},{"id":"3"}]}"""));
        var source = new NekosMoeSource(http);

        var items = await source.GetImagesAsync(NsfwMode.ShowEverything, count: 2);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Dmoe_GetRandomImage_ExtractsImgurl()
    {
        using var http = CreateHttp(_ => JsonResponse("""{"imgurl":"https://example.com/a/b.png"}"""));
        var source = new DmoeSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.ShowEverything);

        Assert.NotNull(item);
        Assert.Equal("https://example.com/a/b.png", item.Url);
        Assert.Equal("png", item.Extension);
        Assert.Null(item.ThumbnailUrl);
    }

    [Fact]
    public async Task WaifuIm_BlockNsfw_UsesIsNsfwFalse()
    {
        string? requestUrl = null;
        using var http = CreateHttp(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return JsonResponse("""{"items":[{"id":"w1","url":"https://i.waifu.im/x.jpg"}]}""");
        });
        var source = new WaifuImSource(http);

        await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Contains("IsNsfw=False", requestUrl);
    }

    [Fact]
    public async Task WaifuIm_NumericId_DoesNotThrow()
    {
        // waifu.im 的 id 是 JSON 数字：必须能正常解析而不抛异常
        using var http = CreateHttp(_ => JsonResponse(
            """{"items":[{"id":12345,"url":"https://i.waifu.im/y.jpg","artists":[{"name":"Artist"}]}]}"""));
        var source = new WaifuImSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.NotNull(item);
        Assert.Equal("12345", item!.Id);
        Assert.Equal("Artist", item.Artist);
        Assert.Null(item.ThumbnailUrl);
    }

    [Fact]
    public async Task Danbooru_GetImages_FiltersForbiddenTags()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """
            [
              {"id":1,"file_url":"https://d.example/a.jpg","tag_string":"cat_ears solo","tag_string_artist":"A"},
              {"id":2,"file_url":"https://d.example/b.jpg","tag_string":"loli school_uniform","tag_string_artist":"B"},
              {"id":3,"file_url":"https://d.example/c.jpg","tag_string":"1girl","tag_string_artist":"C"}
            ]
            """));
        var source = new DanbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.ShowEverything, count: 10);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Id == "2");
    }

    [Fact]
    public void Danbooru_RemoveForbiddenTags_StripsCensoredWords()
    {
        Assert.Equal("cat_ears solo", DanbooruSource.RemoveForbiddenTags("cat_ears solo loli shota"));
        Assert.Equal(string.Empty, DanbooruSource.RemoveForbiddenTags("loli"));
        Assert.True(DanbooruSource.ContainsForbiddenTag("solo loli"));
        Assert.False(DanbooruSource.ContainsForbiddenTag("cat_ears solo"));
    }

    [Fact]
    public async Task Danbooru_BlockNsfw_AppendsRatingGeneral()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":1,"file_url":"https://d.example/a.jpg"}]""");
        });
        var source = new DanbooruSource(http);

        await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Contains("rating%3Ageneral", query);
    }

    [Fact]
    public async Task Lolicon_GetRandomImage_ReturnsPixivUrlAndLink()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """
            {"error":"","data":[{"pid":123456789,"p":0,"author":"Pixiv Author","urls":{"original":"https://i.pixiv.re/img/original/123456789_p0.png"}}]}
            """));
        var source = new LoliconSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.NotNull(item);
        Assert.Equal("123456789_0", item.Id);
        Assert.Equal("Pixiv Author", item.Artist);
        Assert.Equal("https://www.pixiv.net/artworks/123456789", item.SourceLink);
        Assert.Equal("png", item.Extension);
    }

    [Fact]
    public async Task Safebooru_Page_IsZeroBased()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":7,"file_url":"https://safebooru.org/images/a.jpg","preview_url":"https://safebooru.org/thumbnails/7/t.jpg"}]""");
        });
        var source = new SafebooruSource(http);

        var items = await source.GetImagesPageAsync(NsfwMode.ShowEverything, page: 1, perPage: 12);

        Assert.Contains("pid=0", query);
        Assert.Equal("https://safebooru.org/thumbnails/7/t.jpg", items[0].ThumbnailUrl);
    }

    [Fact]
    public async Task Danbooru_ExposesThumbnailUrl()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """[{"id":9,"file_url":"https://d.example/a.jpg","preview_file_url":"https://d.example/prev/a.jpg"}]"""));
        var source = new DanbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.ShowEverything, count: 1);

        Assert.Equal("https://d.example/prev/a.jpg", items[0].ThumbnailUrl);
    }

    [Fact]
    public async Task YandeRe_BlockNsfw_AddsRatingSafe()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":42,"file_url":"https://yande.re/image/a.jpg","author":"YA"}]""");
        });
        var source = new YandeReSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Contains("rating%3Asafe", query);
        Assert.Equal("YA", item?.Artist);
    }

    [Fact]
    public async Task Konachan_BlockNsfw_AddsRatingSafe()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":7,"file_url":"https://konachan.com/image/a.jpg","author":"KA"}]""");
        });
        var source = new KonachanSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Contains("rating%3Asafe", query);
        Assert.Equal("KA", item?.Artist);
        Assert.Equal("https://konachan.com/post/show/7", item?.SourceLink);
    }

    [Fact]
    public async Task Gelbooru_ParsesPostObject()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """
            {"@attributes":{"count":1},"post":[{"id":123,"file_url":"https://img.gelbooru.com/a.png","preview_url":"https://img.gelbooru.com/t.png","owner":"Owner","rating":"safe"}]}
            """));
        var source = new GelbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, count: 1);

        var item = Assert.Single(items);
        Assert.Equal("https://img.gelbooru.com/a.png", item.Url);
        Assert.Equal("https://img.gelbooru.com/t.png", item.ThumbnailUrl);
        Assert.Equal("Owner", item.Artist);
        Assert.Equal("123", item.Id);
        Assert.Equal("png", item.Extension);
    }

    [Fact]
    public async Task Gelbooru_Page_IsZeroBased()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""{"post":[]}""");
        });
        var source = new GelbooruSource(http);

        await source.GetImagesPageAsync(NsfwMode.ShowEverything, page: 1, perPage: 12);

        Assert.Contains("pid=0", query);
        Assert.Contains("limit=12", query);
    }

    [Fact]
    public async Task Source_SetsLastError_OnHttpFailure()
    {
        using var http = CreateHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var source = new NekosMoeSource(http);

        var item = await source.GetRandomImageAsync(NsfwMode.BlockNsfw);

        Assert.Null(item);
        Assert.Contains("503", source.LastError);
    }

    [Fact]
    public async Task WaifuIm_GetImages_UsesRandomPage()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""{"items":[{"id":1,"url":"https://i.waifu.im/x.jpg"}]}""");
        });
        var source = new WaifuImSource(http);

        await source.GetImagesAsync(NsfwMode.BlockNsfw, count: 4);

        Assert.Contains("Page=", query);
    }

    [Fact]
    public async Task Moebooru_GetImages_UsesRandomPage()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":1,"file_url":"https://yande.re/image/a.jpg"}]""");
        });
        var source = new YandeReSource(http);

        await source.GetImagesAsync(NsfwMode.BlockNsfw, count: 4);

        Assert.Contains("page=", query);
    }

    [Fact]
    public async Task Safebooru_GetImages_UsesRandomPid()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""[{"id":1,"file_url":"https://safebooru.org/a.jpg"}]""");
        });
        var source = new SafebooruSource(http);

        await source.GetImagesAsync(NsfwMode.BlockNsfw, count: 4);

        Assert.Contains("pid=", query);
    }

    [Fact]
    public async Task Gelbooru_GetImages_UsesRandomPid()
    {
        string? query = null;
        using var http = CreateHttp(req =>
        {
            query = req.RequestUri!.Query;
            return JsonResponse("""{"post":[{"id":1,"file_url":"https://img.gelbooru.com/a.jpg"}]}""");
        });
        var source = new GelbooruSource(http);

        await source.GetImagesAsync(NsfwMode.BlockNsfw, count: 4);

        Assert.Contains("pid=", query);
    }

    [Fact]
    public void ImageItem_InferExtensionFromUrl_HandlesQuery()
    {
        var item = new ImageItem("https://example.com/img/abc.png?size=full");

        Assert.Equal("png", item.InferExtensionFromUrl());
    }

    [Fact]
    public async Task ImageDownloader_RejectsOversizedContent()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new ByteArrayContent(new byte[1024]);
            response.Content.Headers.ContentLength = ImageDownloader.MaxDownloadBytes + 1;
            return response;
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync("https://example.com/big.jpg"));
    }

    [Fact]
    public async Task ImageDownloader_StreamsContentWithinLimit()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new ByteArrayContent(new byte[4096]);
            return response;
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(http);

        var bytes = await downloader.DownloadAsync("https://example.com/ok.jpg");

        Assert.Equal(4096, bytes.Length);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        var node = JsonNode.Parse(json);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StringContent(node?.ToJsonString() ?? json, System.Text.Encoding.UTF8, "application/json");
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
