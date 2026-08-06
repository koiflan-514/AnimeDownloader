using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

// 验证：① 缩略图 URL 字段已解析且比原图小；② dmoe 并行拉取速度。
var settings = new AppSettings();
var factory = new HttpClientFactory(settings);
using var http = factory.CreateClient();
var sources = SourceRegistry.CreateAll(http);
var downloader = new ImageDownloader(http);

// 1) safebooru 缩略图 vs 原图大小对比
var sb = sources.First(s => s.Id == "safebooru");
var sbItems = await sb.GetImagesPageAsync(NsfwMode.BlockNsfw, page: 1, perPage: 1);
if (sbItems.Count > 0)
{
    var item = sbItems[0];
    Console.WriteLine($"safebooru thumb={item.ThumbnailUrl}");
    if (!string.IsNullOrEmpty(item.ThumbnailUrl))
    {
        var t = await downloader.DownloadAsync(item.ThumbnailUrl);
        var f = await downloader.DownloadAsync(item.Url);
        Console.WriteLine($"  thumb {t.Length} bytes vs full {f.Length} bytes");
    }
}

// 2) dmoe 并行拉取 6 张计时
var dmoe = sources.First(s => s.Id == "dmoe");
var sw = System.Diagnostics.Stopwatch.StartNew();
var dmoeItems = await dmoe.GetImagesAsync(NsfwMode.BlockNsfw, count: 6);
sw.Stop();
Console.WriteLine($"dmoe 6 items in {sw.ElapsedMilliseconds}ms (parallel)");
return 0;
