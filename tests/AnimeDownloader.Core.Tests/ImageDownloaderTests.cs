using System.Net;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Tests;

public class ImageDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_RetriesTransientFailures()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : OkBytes(new byte[] { 1, 2, 3 });
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(
            http,
            new ImageDownloaderOptions { RetryCount = 2, EnableThumbnailCache = false });

        var bytes = await downloader.DownloadAsync("https://example.com/retry.jpg");

        Assert.Equal(3, attempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryClientErrors()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(
            http,
            new ImageDownloaderOptions { RetryCount = 3, EnableThumbnailCache = false });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync("https://example.com/missing.jpg"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DownloadCachedAsync_HitsMemoryCache()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return OkBytes(new byte[] { 9, 9, 9 });
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(http);

        var first = await downloader.DownloadCachedAsync("https://example.com/cached.jpg");
        var second = await downloader.DownloadCachedAsync("https://example.com/cached.jpg");

        Assert.Equal(1, attempts);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DownloadCachedAsync_CoalescesConcurrentRequests()
    {
        var attempts = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelayedHandler(async cancellationToken =>
        {
            Interlocked.Increment(ref attempts);
            await gate.Task.WaitAsync(cancellationToken);
            return OkBytes(new byte[] { 4, 2 });
        });
        using var http = new HttpClient(handler);
        var downloader = new ImageDownloader(http);

        var first = downloader.DownloadCachedAsync("https://example.com/shared.jpg");
        var second = downloader.DownloadCachedAsync("https://example.com/shared.jpg");
        gate.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DownloadToFileAsync_WritesAtomically()
    {
        var dir = CreateTempDir();
        try
        {
            var payload = new byte[4096];
            Random.Shared.NextBytes(payload);
            var handler = new StubHandler(_ => OkBytes(payload));
            using var http = new HttpClient(handler);
            var downloader = new ImageDownloader(
                http,
                new ImageDownloaderOptions { EnableThumbnailCache = false });

            var target = Path.Combine(dir, "image.bin");
            await downloader.DownloadToFileAsync("https://example.com/file.bin", target);

            Assert.Equal(payload, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"), "temp file should be cleaned up");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchDownloader_SkipsExistingAndSavesNew()
    {
        var dir = CreateTempDir();
        try
        {
            var existing = Path.Combine(dir, "1.jpg");
            File.WriteAllBytes(existing, new byte[] { 5 });

            var handler = new StubHandler(_ => OkBytes(new byte[] { 7, 7 }));
            using var http = new HttpClient(handler);
            var downloader = new ImageDownloader(
                http,
                new ImageDownloaderOptions { EnableThumbnailCache = false });
            var batch = new BatchDownloader(downloader);
            var items = new List<ImageItem>
            {
                new("https://example.com/a.jpg", Id: "1", Extension: "jpg"),
                new("https://example.com/b.jpg", Id: "2", Extension: "jpg"),
            };

            var result = await batch.DownloadAllAsync(items, dir);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(Path.Combine(dir, "1.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "2.jpg")));
            Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(Path.Combine(dir, "2.jpg")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImageItem_SuggestFileName_UsesIdAndExtension()
    {
        Assert.Equal("abc_0.png", new ImageItem("https://x/i.png", Id: "abc_0", Extension: "png").SuggestFileName());
        Assert.Equal("image_1.jpg", new ImageItem("https://x/i.jpg", Id: "image_1").SuggestFileName());
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnimeDownloaderDownloaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HttpResponseMessage OkBytes(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(bytes);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class DelayedHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(cancellationToken);
    }
}
