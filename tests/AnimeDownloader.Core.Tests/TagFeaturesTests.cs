using System.Net;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.Core.Tests;

/// <summary>
/// 标签升级功能的纯逻辑测试：帖子全标签解析、标签联想 / 相关标签解析、
/// 内嵌热门标签中文表。全部使用本地 Stub 应答，不访问真实网络。
/// </summary>
public class TagFeaturesTests
{
    private static HttpClient CreateHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StubHandler(responder));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeDownloaderTest/1.0");
        return client;
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    // ---------------- 帖子全标签解析 ----------------

    [Fact]
    public async Task Danbooru_PostTags_CarryCategories()
    {
        using var http = CreateHttp(_ => JsonResponse("""
            [{
                "id": 1,
                "file_url": "https://cdn.donmai.us/a.jpg",
                "tag_string": "hatsune_miku vocaloid green_foo",
                "tag_string_artist": "green_foo",
                "tag_string_character": "hatsune_miku",
                "tag_string_copyright": "vocaloid"
            }]
            """));
        var source = new DanbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal(3, item.TagList.Count);
        Assert.Contains(item.TagList, t => t.Name == "hatsune_miku" && t.Category == TagCategory.Character);
        Assert.Contains(item.TagList, t => t.Name == "vocaloid" && t.Category == TagCategory.Copyright);
        Assert.Contains(item.TagList, t => t.Name == "green_foo" && t.Category == TagCategory.Artist);
    }

    [Fact]
    public async Task NekosMoe_PostTags_ParsedFromArray()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"images":[{"id":"abc","tags":["catgirl","blue_eyes"]}]}"""));
        var source = new NekosMoeSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal(2, item.TagList.Count);
        Assert.Contains(item.TagList, t => t.Name == "catgirl");
    }

    [Fact]
    public async Task Gelbooru_PostTags_ParsedFromString()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"@attributes":{},"post":[{"id":9,"file_url":"https://gelbooru.com/x.jpg","tags":"blue_hair smile"}]}"""));
        var source = new GelbooruSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal(2, item.TagList.Count);
        Assert.Contains(item.TagList, t => t.Name == "blue_hair");
        Assert.Contains(item.TagList, t => t.Name == "smile");
    }

    [Fact]
    public async Task Moebooru_PostTags_ParsedFromString()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """[{"id":5,"file_url":"https://yande.re/y.jpg","tags":"thighhighs dress"}]"""));
        var source = new YandeReSource(http);

        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 1);

        var item = Assert.Single(items);
        Assert.Equal(2, item.TagList.Count);
        Assert.Contains(item.TagList, t => t.Name == "thighhighs");
    }

    // ---------------- 标签联想 ----------------

    [Fact]
    public async Task Danbooru_SuggestTags_ParsesCountCategoryAndChinese()
    {
        using var http = CreateHttp(req =>
        {
            Assert.Contains("tags.json", req.RequestUri!.PathAndQuery);
            Assert.Contains("name_matches", req.RequestUri.Query);
            Assert.Contains("kanta", req.RequestUri.Query);
            return JsonResponse(
                """[{"name":"kantai_collection","post_count":123456,"category":3},{"name":"kanta_x","post_count":7,"category":0}]""");
        });
        var source = new DanbooruSource(http);

        var suggestions = await source.SuggestTagsAsync("kanta", 10);

        Assert.True(source.SupportsTagSuggestions);
        Assert.Equal(2, suggestions.Count);
        var first = suggestions[0];
        Assert.Equal("kantai_collection", first.Name);
        Assert.Equal(123456, first.PostCount);
        Assert.Equal(TagCategory.Copyright, first.Category);
        Assert.NotNull(first.ChineseName);
    }

    [Fact]
    public async Task Moebooru_SuggestTags_ParsesTypeField()
    {
        using var http = CreateHttp(req =>
        {
            Assert.Contains("/tag.json", req.RequestUri!.PathAndQuery);
            return JsonResponse(
                """[{"name":"hatsune_miku","count":99999,"type":4}]""");
        });
        var source = new YandeReSource(http);

        var suggestions = await source.SuggestTagsAsync("hatsune", 10);

        var first = Assert.Single(suggestions);
        Assert.Equal("hatsune_miku", first.Name);
        Assert.Equal(99999, first.PostCount);
        Assert.Equal(TagCategory.Character, first.Category);
    }

    [Fact]
    public async Task Gelbooru_SuggestTags_WithoutCredentials_FallsBackToLocalTable()
    {
        using var http = CreateHttp(_ => throw new InvalidOperationException("无凭据时不应发起在线联想"));
        var source = new GelbooruSource(http);

        Assert.True(source.SupportsTagSuggestions);
        var suggestions = await source.SuggestTagsAsync("hatsune");

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.Name == "hatsune_miku" && s.ChineseName is not null);
    }

    [Fact]
    public async Task Gelbooru_SuggestTags_WithCredentials_ParsesType()
    {
        string? requestUrl = null;
        using var http = CreateHttp(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return JsonResponse("""{"tag":[{"name":"hatsune_miku","count":50000,"type":3}]}""");
        });
        var source = new GelbooruSource(http) { UserId = "000000001", ApiKey = new string('0', 8) };

        Assert.True(source.SupportsTagSuggestions);
        var suggestions = await source.SuggestTagsAsync("hatsune");

        var first = Assert.Single(suggestions);
        Assert.Equal(TagCategory.Character, first.Category);
        Assert.NotNull(requestUrl);
        Assert.Contains("user_id=000000001", requestUrl);
        Assert.Contains("api_key=00000000", requestUrl);
        Assert.Contains("s=tag", requestUrl);
    }

    [Fact]
    public async Task Safebooru_SuggestTags_UsesLocalPrefixTable()
    {
        // Safebooru 的 dapi tag 查询不支持通配符且 json 输出损坏，联想走本地表
        using var http = CreateHttp(_ => throw new InvalidOperationException("Safebooru 联想不应发起网络请求"));
        var source = new SafebooruSource(http);

        var suggestions = await source.SuggestTagsAsync("smi");

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.Name == "smile" && s.ChineseName == "微笑");
        Assert.All(suggestions, s => Assert.StartsWith("smi", s.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 相关标签 ----------------

    [Fact]
    public async Task Danbooru_RelatedTags_ParsesNestedTagObject()
    {
        using var http = CreateHttp(_ => JsonResponse("""
            {"related_tags":[
                {"tag":{"name":"vocaloid","post_count":555,"category":3}},
                {"tag":{"name":"twintails","post_count":222,"category":0}}
            ]}
            """));
        var source = new DanbooruSource(http);

        var related = await source.GetRelatedTagsAsync("hatsune_miku");

        Assert.Equal(2, related.Count);
        Assert.Contains(related, t => t.Name == "vocaloid" && t.Category == TagCategory.Copyright);
        Assert.Contains(related, t => t.Name == "twintails");
    }

    [Fact]
    public async Task Moebooru_RelatedTags_ParsesTripleArrays()
    {
        using var http = CreateHttp(_ => JsonResponse(
            """{"tags":{"hatsune_miku":[["vocaloid",555,3],["twintails",222,0]]}}"""));
        var source = new YandeReSource(http);

        var related = await source.GetRelatedTagsAsync("hatsune_miku");

        Assert.Equal(2, related.Count);
        Assert.Contains(related, t => t.Name == "vocaloid" && t.PostCount == 555);
        Assert.Contains(related, t => t.Name == "twintails");
    }

    // ---------------- 热门标签中文表 ----------------

    [Fact]
    public void TagLocalization_PopularTags_HaveChineseNames()
    {
        Assert.True(TagLocalization.TryGet("1girl", out var entry));
        Assert.False(string.IsNullOrWhiteSpace(entry.ChineseName));
        Assert.True(entry.PostCount > 0);
    }

    [Fact]
    public void TagLocalization_UnknownTag_ReturnsFalse()
    {
        Assert.False(TagLocalization.TryGet("definitely_not_a_real_tag_xyz_123", out _));
    }

    [Fact]
    public void Danbooru_Suggestion_IncludesChineseForPopularTags()
    {
        // Danbooru_SuggestTags_ParsesCountCategoryAndChinese 已验证 kantai_collection 有中文，
        // 这里补充验证 1girl（表中热度第一）作为回归保护。
        Assert.True(TagLocalization.TryGet("solo", out var entry));
        Assert.Equal("单人", entry.ChineseName);
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
