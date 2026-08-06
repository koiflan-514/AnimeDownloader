using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

// 验证用户实际配置（safebooru + paged）能否拉到图且字节可解码为图片。
var settings = new AppSettings { SelectedSource = "safebooru", GallerySubmode = "paged" };
var factory = new HttpClientFactory(settings);
using var http = factory.CreateClient();
var sources = SourceRegistry.CreateAll(http);
var source = sources.First(s => s.Id == "safebooru");
var downloader = new ImageDownloader(http);

var items = await source.GetImagesPageAsync(NsfwMode.BlockNsfw, page: 1, perPage: 3);
Console.WriteLine($"safebooru paged: {items.Count} items");
foreach (var item in items)
{
    var bytes = await downloader.DownloadAsync(item.Url);
    // 用 BitmapImage 相同解码路径验证：检查文件头（JPEG FF D8 / PNG 89 50 4E 47）
    var isJpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
    var isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
    Console.WriteLine($"  {item.Url} -> {bytes.Length} bytes, jpeg={isJpeg} png={isPng}");
}
