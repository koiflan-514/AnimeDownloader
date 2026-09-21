using Android.Graphics;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.App.Thumbnails;

/// <summary>
/// 缩略图加载器：网络传输 + 采样解码 + 位图 LRU。
/// </summary>
/// <remarks>
/// <para><b>这个类的存在理由是把「手机上别 OOM」这件事做对。</b>
/// 一张 1500×2000 的图直接解码大约是 12MB（ARGB_8888），一个 24 张的画廊就是 288MB ——
/// 中端手机的整个进程堆上限也就 128–256MB。所以必须做三件事：</para>
///
/// <list type="number">
///   <item><description><b>采样解码（inSampleSize）</b>：先只读尺寸（<c>InJustDecodeBounds</c>），
///   算出「解码后不小于目标尺寸」的最大 2 的幂次采样率。<b>不是</b>解码完整图再缩放 ——
///   那样峰值内存就已经爆了。1500×2000 解到 238px 卡片上，采样率是 8，内存降到 1/64。</description></item>
///   <item><description><b>位图 LRU</b>：按<b>字节数</b>而不是条目数设上限。条目数上限在手机上
///   没有意义 —— 256 张 4MB 的图和 256 张 60KB 的缩略图是一个数量级的「条」，
///   却是两个数量级的内存。上限取可用 Java 堆的 1/8，再压到 48MB 以内。</description></item>
///   <item><description><b>并发闸门</b>：列表快速滑动时会瞬间产生几十个加载请求。
///   不设限的话，同时解码十几张大图足以触发低内存回收 ——
///   症状是「快速滑动后应用被杀」，而正常浏览一切正常。这里限 3。</description></item>
/// </list>
///
/// <para><b>磁盘缓存不在这里</b>：<see cref="ImageDownloader.DownloadCachedAsync"/> 已经带了
/// 内存 LRU + 磁盘层（上限 2000 个文件），直接复用即可 —— 那是 Core 里两个桌面工程
/// 也在用的同一条路径。</para>
///
/// <para><b>故意不调用 <c>Bitmap.Recycle()</c></b>：现代 Android 上位图在 Java 堆上，
/// 由 GC 回收即可。而被 LRU 淘汰的位图<b>可能仍挂在某个正在显示的 ImageView 上</b> ——
/// 此时 Recycle 会让绘制阶段抛「trying to use a recycled bitmap」。
/// 淘汰不等于没人用，这个区别是这类崩溃的常见来源。</para>
/// </remarks>
internal sealed class ThumbnailLoader : IDisposable
{
    /// <summary>同时进行的解码/下载请求数上限。</summary>
    private const int MaxConcurrentRequests = 3;

    /// <summary>位图缓存的内存上限兜底值。</summary>
    private const int MaxCacheBytes = 48 * 1024 * 1024;

    private readonly ImageDownloader _downloader;
    private readonly BitmapLru _cache;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private bool _disposed;

    internal ThumbnailLoader(ImageDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _downloader = downloader;
        _cache = new BitmapLru(ResolveCacheBudget());
    }

    /// <summary>当前缓存的字节数与条目数（用于设置页的「清理缓存」提示）。</summary>
    internal (long Bytes, int Count) Stats => _cache.Stats;

    /// <summary>
    /// 加载一张缩略图。
    /// </summary>
    /// <param name="url">图片地址（图源给的缩略图地址，或原图地址）。</param>
    /// <param name="targetPx">目标边长（px）—— 采样率按它算，取值应等于格子边长而不是屏幕尺寸。</param>
    /// <param name="cancellationToken">调用方（卡片视图）被回收时取消。</param>
    /// <returns>解码好的位图；失败或取消时返回 null（界面显示占位，不抛异常）。</returns>
    internal async Task<Bitmap?> LoadAsync(string url, int targetPx, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = $"{url}@{targetPx}";
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 拿到闸门后再查一次：排队期间同格子可能已经被别的请求填好了。
            if (_cache.TryGet(key, out cached))
            {
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 走 Core 的带缓存下载：命中磁盘层时完全不联网。
            var bytes = await _downloader
                .DownloadCachedAsync(url, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = DecodeSampled(bytes, targetPx);
            if (bitmap is not null)
            {
                _cache.Set(key, bitmap);
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // 网络、HTTP、解码失败一律降级为「显示占位」——单张缩略图失败不该在界面上炸开。
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清空位图缓存与 Core 侧的磁盘缓存。</summary>
    internal void Clear()
    {
        _cache.Clear();
        _downloader.ClearThumbnailCache();
    }

    /// <summary>
    /// 采样解码：两趟读取，先拿尺寸再按 2 的幂次降采样。
    /// </summary>
    internal static Bitmap? DecodeSampled(byte[] bytes, int targetPx)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            return null;
        }

        var options = new BitmapFactory.Options
        {
            InSampleSize = ResolveSampleSize(bounds.OutWidth, bounds.OutHeight, targetPx),
            InPreferredConfig = Bitmap.Config.Argb8888,
        };
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
    }

    /// <summary>
    /// 算出「解码后不小于目标尺寸」的最大 2 的幂次采样率。
    /// </summary>
    /// <remarks>
    /// <c>InSampleSize</c> 只接受 2 的幂次（其它值会被向下取整到 2 的幂），
    /// 所以这里直接按倍数递推。<b>不放大</b>：原图比目标还小时返回 1，
    /// 让 <c>ImageView</c> 的缩放去处理 —— 采样率写成 0 或负数会直接抛异常。
    /// </remarks>
    internal static int ResolveSampleSize(int width, int height, int targetPx)
    {
        if (targetPx <= 0)
        {
            return 1;
        }

        var longest = Math.Max(width, height);
        var sample = 1;
        while (longest / (sample * 2) >= targetPx)
        {
            sample *= 2;
        }

        return sample;
    }

    private static int ResolveCacheBudget()
    {
        try
        {
            // 位图在 Java 堆上，所以按 Java 堆上限算比例，而不是进程的 native 上限。
            var maxHeap = Java.Lang.Runtime.GetRuntime()?.MaxMemory() ?? MaxCacheBytes;
            var budget = maxHeap / 8;
            return (int)Math.Clamp(budget, 8L * 1024 * 1024, MaxCacheBytes);
        }
        catch (Exception)
        {
            return MaxCacheBytes;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        _gate.Dispose();
    }

    /// <summary>
    /// 按<b>字节数</b>计量的位图 LRU。
    /// </summary>
    /// <remarks>
    /// 没用 <c>Android.Util.LruCache</c>：它的 <c>SizeOf</c> 要子类化 Java 类才能覆写，
    /// 在这个纯 C# 的界面层里绕一层不划算。三十行手写，行为可控，也直接可测。
    /// </remarks>
    private sealed class BitmapLru(int maxBytes)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _order = new();
        private long _bytes;

        public (long Bytes, int Count) Stats
        {
            get
            {
                lock (_lock)
                {
                    return (_bytes, _index.Count);
                }
            }
        }

        public bool TryGet(string key, out Bitmap? bitmap)
        {
            lock (_lock)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    // 命中即提到队首：LRU 的「最近使用」语义靠这一步维持。
                    _order.Remove(node);
                    _order.AddFirst(node);
                    bitmap = node.Value.Bitmap;
                    return true;
                }
            }

            bitmap = null;
            return false;
        }

        public void Set(string key, Bitmap bitmap)
        {
            var size = Math.Max(1, BitmapBytes(bitmap));
            lock (_lock)
            {
                if (_index.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _index.Remove(key);
                    _bytes -= existing.Value.Bytes;
                }

                var node = _order.AddFirst(new Entry(key, bitmap, size));
                _index[key] = node;
                _bytes += size;

                while (_bytes > maxBytes && _order.Last is { } last)
                {
                    _order.RemoveLast();
                    _index.Remove(last.Value.Key);
                    _bytes -= last.Value.Bytes;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _index.Clear();
                _order.Clear();
                _bytes = 0;
            }
        }

        /// <summary>
        /// 位图占用的字节数。API 26 起用 <c>AllocationByteCount</c>（它把复用的位图算准了），
        /// 更低版本退回 <c>ByteCount</c>。
        /// </summary>
        private static int BitmapBytes(Bitmap bitmap) => OperatingSystem.IsAndroidVersionAtLeast(26)
            ? bitmap.AllocationByteCount
            : bitmap.ByteCount;

        private readonly record struct Entry(string Key, Bitmap Bitmap, long Bytes);
    }
}
