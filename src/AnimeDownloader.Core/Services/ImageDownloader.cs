using System.Net;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// Download progress information.
/// </summary>
/// <param name="DownloadedBytes">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Total bytes when known, otherwise null.</param>
public readonly record struct DownloadProgress(long DownloadedBytes, long? TotalBytes)
{
    public double? Percent =>
        TotalBytes is > 0 ? (double)DownloadedBytes / TotalBytes.Value : null;
}

/// <summary>Options controlling concurrency, retries and the thumbnail cache.</summary>
public sealed class ImageDownloaderOptions
{
    /// <summary>Maximum concurrent download operations (clamped to at least 1).</summary>
    public int MaxConcurrentDownloads { get; set; } = 4;

    /// <summary>Number of retries for transient failures (clamped to at least 0).</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Whether the in-memory + disk thumbnail cache is enabled.</summary>
    public bool EnableThumbnailCache { get; set; } = true;

    /// <summary>Directory used for the disk-backed portion of the thumbnail cache.</summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>Aggregate progress reported by <see cref="BatchDownloader"/>.</summary>
/// <param name="Completed">Items processed so far.</param>
/// <param name="Total">Total items in the batch.</param>
/// <param name="CurrentName">Display name of the item currently being processed.</param>
/// <param name="Current">Per-file progress, when available.</param>
public readonly record struct BatchDownloadProgress(
    int Completed,
    int Total,
    string? CurrentName,
    DownloadProgress? Current);

/// <summary>Result of a batch download operation.</summary>
/// <param name="Succeeded">Files saved (or already present).</param>
/// <param name="Failed">Items that could not be downloaded.</param>
/// <param name="SavedFiles">Absolute paths of saved files, in completion order.</param>
public readonly record struct BatchDownloadResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string> SavedFiles);

/// <summary>
/// Streams image bytes to memory or disk with optional progress reporting, retries,
/// bounded concurrency, and a thumbnail cache (memory LRU plus an optional disk tier).
/// </summary>
public sealed class ImageDownloader : IDisposable
{
    /// <summary>Single-image download cap (guards against hostile oversized responses).</summary>
    public const long MaxDownloadBytes = 200 * 1024 * 1024;

    private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(400);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;
    private readonly int _retryCount;
    private readonly ThumbnailCache? _cache;
    private readonly object _thumbnailLock = new();
    private readonly Dictionary<string, Task<byte[]>> _thumbnailRequests = new(StringComparer.Ordinal);

    public ImageDownloader(HttpClient http, ImageDownloaderOptions? options = null)
    {
        _http = http;
        options ??= new ImageDownloaderOptions();
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentDownloads));
        _retryCount = Math.Max(0, options.RetryCount);
        if (options.EnableThumbnailCache)
        {
            _cache = new ThumbnailCache(options.CacheDirectory);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into a byte array, honoring the concurrency gate and
    /// retrying transient failures.
    /// </summary>
    /// <exception cref="HttpRequestException">Network error or non-success status code.</exception>
    /// <exception cref="InvalidOperationException">Response exceeds <see cref="MaxDownloadBytes"/>.</exception>
    public Task<byte[]> DownloadAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(
            url,
            (u, p, ct) => DownloadToMemoryAsync(u, p, ct),
            progress,
            cancellationToken);

    /// <summary>
    /// Downloads <paramref name="url"/> through the thumbnail cache. Cached hits skip the network.
    /// </summary>
    public Task<byte[]> DownloadCachedAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_cache is null)
        {
            return DownloadAsync(url, progress, cancellationToken);
        }

        if (_cache.TryGet(url, out var hit))
        {
            return Task.FromResult(hit);
        }

        Task<byte[]> request;
        lock (_thumbnailLock)
        {
            if (!_thumbnailRequests.TryGetValue(url, out request!))
            {
                request = DownloadAndCacheSharedAsync(url, progress);
                _thumbnailRequests[url] = request;
            }
        }

        return cancellationToken.CanBeCanceled ? request.WaitAsync(cancellationToken) : request;
    }

    private async Task<byte[]> DownloadAndCacheSharedAsync(
        string url,
        IProgress<DownloadProgress>? progress)
    {
        try
        {
            var bytes = await DownloadAsync(url, progress, CancellationToken.None).ConfigureAwait(false);
            _cache!.Set(url, bytes);
            return bytes;
        }
        finally
        {
            lock (_thumbnailLock)
            {
                _thumbnailRequests.Remove(url);
            }
        }
    }

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="destinationPath"/> atomically
    /// (temp file + rename), with the same retry and concurrency behavior as byte downloads.
    /// </summary>
    public Task DownloadToFileAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(
            url,
            (u, p, ct) => DownloadToFileCoreAsync(u, destinationPath, p, ct),
            progress,
            cancellationToken);

    private async Task<T> DownloadCoreAsync<T>(
        string url,
        Func<string, IProgress<DownloadProgress>?, CancellationToken, Task<T>> operation,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt <= _retryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await operation(url, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt < _retryCount)
                {
                    lastError = ex;
                    await Task.Delay(BackoffBase * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }

            throw lastError ?? new HttpRequestException("Download failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> DownloadToMemoryAsync(
        string url,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await OpenResponseAsync(url, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(
            stream,
            buffer,
            response.Content.Headers.ContentLength,
            progress,
            cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<bool> DownloadToFileCoreAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await OpenResponseAsync(url, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destinationPath + ".part";
        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await CopyWithLimitAsync(
                    stream,
                    file,
                    response.Content.Headers.ContentLength,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }

        return true;
    }

    private async Task<HttpResponseMessage> OpenResponseAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"HTTP {(int)status} {response.ReasonPhrase} for {url}",
                inner: null,
                statusCode: (HttpStatusCode)status);
        }

        var total = response.Content.Headers.ContentLength;
        if (total is > MaxDownloadBytes)
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"Image too large ({MaxDownloadBytes / (1024 * 1024)}MB limit), download aborted.");
        }

        return response;
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long? total,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            downloaded += read;
            if (downloaded > MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    $"Image too large ({MaxDownloadBytes / (1024 * 1024)}MB limit), download aborted.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(downloaded, total));
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException when !cancellationToken.IsCancellationRequested => true,
        HttpRequestException hre => hre.StatusCode is null ||
            hre.StatusCode == HttpStatusCode.RequestTimeout ||
            hre.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)hre.StatusCode.Value >= 500,
        IOException => true,
        _ => false,
    };

    /// <summary>Clears both the in-memory and disk thumbnail cache.</summary>
    public void ClearThumbnailCache()
    {
        _cache?.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}

/// <summary>
/// Small LRU byte cache used for gallery thumbnails. A fixed-size in-memory tier is always
/// available; an optional disk tier persists entries between launches.
/// </summary>
internal sealed class ThumbnailCache
{
    private const int MaxMemoryEntries = 256;
    private const int MaxDiskFiles = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly string? _diskDirectory;

    public ThumbnailCache(string? diskDirectory)
    {
        if (!string.IsNullOrWhiteSpace(diskDirectory))
        {
            try
            {
                Directory.CreateDirectory(diskDirectory);
                _diskDirectory = diskDirectory;
            }
            catch (IOException)
            {
                _diskDirectory = null;
            }
            catch (UnauthorizedAccessException)
            {
                _diskDirectory = null;
            }
        }
    }

    public bool TryGet(string key, out byte[] bytes)
    {
        bytes = null!;
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                node.Value.Touch();
                bytes = node.Value.Bytes;
                return true;
            }
        }

        if (_diskDirectory is null)
        {
            return false;
        }

        var path = DiskPath(key);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > ImageDownloader.MaxDownloadBytes)
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            lock (_lock)
            {
                AddMemory(key, bytes);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Set(string key, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            AddMemory(key, bytes);
        }

        if (_diskDirectory is not null)
        {
            TryWriteDisk(key, bytes);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _memory.Clear();
            _lru.Clear();
        }

        if (_diskDirectory is null)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_diskDirectory, "*.img"))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private void AddMemory(string key, byte[] bytes)
    {
        if (_memory.TryGetValue(key, out var existing))
        {
            _lru.Remove(existing);
            _memory.Remove(key);
        }

        var entry = new CacheEntry(key, bytes);
        var node = _lru.AddFirst(entry);
        _memory[key] = node;
        EvictIfNeeded();
    }

    private void EvictIfNeeded()
    {
        while (_memory.Count > MaxMemoryEntries)
        {
            var last = _lru.Last;
            if (last is null)
            {
                break;
            }

            _memory.Remove(last.Value.Key);
            _lru.RemoveLast();
        }
    }

    private string DiskPath(string key)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_diskDirectory!, hash + ".img");
    }

    private void TryWriteDisk(string key, byte[] bytes)
    {
        try
        {
            var path = DiskPath(key);
            File.WriteAllBytes(path, bytes);
            TrimDiskIfNeeded();
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private void TrimDiskIfNeeded()
    {
        try
        {
            var files = Directory.EnumerateFiles(_diskDirectory!, "*.img")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var stale in files.Skip(MaxDiskFiles))
            {
                stale.Delete();
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed class CacheEntry(string key, byte[] bytes)
    {
        public string Key { get; } = key;
        public byte[] Bytes { get; } = bytes;
        public long LastAccessTicks { get; private set; } = Environment.TickCount64;

        public void Touch() => LastAccessTicks = Environment.TickCount64;
    }
}

/// <summary>
/// Downloads a list of <see cref="ImageItem"/> into a directory, skipping files that already
/// exist and reporting aggregate progress. Failures are counted and do not abort the batch.
/// </summary>
public sealed class BatchDownloader
{
    private readonly ImageDownloader _downloader;

    public BatchDownloader(ImageDownloader downloader)
    {
        _downloader = downloader;
    }

    public async Task<BatchDownloadResult> DownloadAllAsync(
        IReadOnlyList<ImageItem> items,
        string directory,
        IProgress<BatchDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(directory);

        Directory.CreateDirectory(directory);
        var saved = new string[items.Count];
        var failed = 0;
        var completed = 0;
        var nextSlot = 0;

        // 并行下载：底层 ImageDownloader 自带并发闸门（MaxConcurrentDownloads），
        // 这里按 CPU 核数铺开任务即可；单条失败只计数、不中断整批。
        var parallelism = Math.Max(2, Environment.ProcessorCount);
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            },
            async (item, ct) =>
            {
                var slot = Interlocked.Increment(ref nextSlot) - 1;
                var target = Path.Combine(directory, item.SuggestFileName());
                try
                {
                    if (File.Exists(target) && new FileInfo(target).Length > 0)
                    {
                        saved[slot] = target;
                    }
                    else
                    {
                        await _downloader.DownloadToFileAsync(
                            item.Url,
                            target,
                            progress: null,
                            ct).ConfigureAwait(false);
                        saved[slot] = target;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }

                progress?.Report(new BatchDownloadProgress(
                    Interlocked.Increment(ref completed),
                    items.Count,
                    item.Artist ?? item.Id,
                    null));
            }).ConfigureAwait(false);

        var succeeded = saved.Count(s => s is not null);
        return new BatchDownloadResult(
            succeeded,
            failed,
            saved.Where(s => s is not null).ToArray());
    }
}