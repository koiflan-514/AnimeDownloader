namespace AnimeDownloader.Core.Services;

/// <summary>
/// 下载进度信息。
/// </summary>
/// <param name="DownloadedBytes">已下载字节数。</param>
/// <param name="TotalBytes">总字节数（未知时为 null）。</param>
public readonly record struct DownloadProgress(long DownloadedBytes, long? TotalBytes)
{
    public double? Percent =>
        TotalBytes is > 0 ? (double)DownloadedBytes / TotalBytes.Value : null;
}

/// <summary>
/// 流式下载图片字节到目标流，支持取消与进度回调。
/// </summary>
public sealed class ImageDownloader
{
    /// <summary>单张图片下载上限（避免恶意/异常响应耗尽内存）。</summary>
    public const long MaxDownloadBytes = 200 * 1024 * 1024;

    private readonly HttpClient _http;

    public ImageDownloader(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 下载 <paramref name="url"/> 的字节内容。
    /// </summary>
    /// <returns>成功时返回字节数组。</returns>
    /// <exception cref="HttpRequestException">网络错误或非成功状态码。</exception>
    /// <exception cref="InvalidOperationException">响应超过 <see cref="MaxDownloadBytes"/> 上限。</exception>
    public async Task<byte[]> DownloadAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        if (total is > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"图片过大（>{MaxDownloadBytes / (1024 * 1024)}MB），已取消下载");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            downloaded += read;
            if (downloaded > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"图片过大（>{MaxDownloadBytes / (1024 * 1024)}MB），已取消下载");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(downloaded, total));
        }

        return buffer.ToArray();
    }
}
