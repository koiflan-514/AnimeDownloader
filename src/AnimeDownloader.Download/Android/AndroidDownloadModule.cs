using Android.App;
using Android.Content;
using AnimeDownloader.Download.Contracts;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Download;

/// <summary>
/// Android 端下载模块的实现。
/// </summary>
/// <remarks>
/// <para><b>它复用了什么</b>：传输层完全交给 Core 的 <see cref="ImageDownloader"/> ——
/// 并发闸门、指数退避重试、<c>.part</c> 临时文件 + 原子改名、缩略图 LRU 缓存都在那里，
/// 桌面端与手机端跑的是同一份代码。本模块只补上「手机独有的那几件事」：
/// 落点（分区存储）、后台保活（前台服务）、通知进度、网络与空间策略、以及更低的并发。</para>
///
/// <para><b>两遍跑法</b>：第一遍按并发闸门把所有项过一遍；失败项如果在末尾仍有剩余，
/// 则再补跑 <see cref="DownloadBatchOptions.RetryCount"/> 轮。这比「某一项原地死等」
/// 更适合手机：断网往往持续几秒到几十秒，原地重试会把并发槽白白占住，
/// 而先跳过、整批走完再统一补跑时，网络通常已经恢复。</para>
///
/// <para><b>快照节流</b>：字节级进度回调非常密集（每 80KB 一次），如果每次都构造一份新的
/// 不可变快照并通知订阅方与通知栏，光是分配与 IPC 就能把滚动帧率拖垮。因此快照按
/// 「状态变化必发、纯进度最多每 150ms 一发」的规则节流，通知栏另按 500ms 节流。</para>
/// </remarks>
public sealed class AndroidDownloadModule : IDownloadModule
{
    /// <summary>纯进度快照的最小发布间隔。</summary>
    private static readonly TimeSpan SnapshotThrottle = TimeSpan.FromMilliseconds(150);

    /// <summary>通知栏进度更新的最小间隔（通知刷新是跨进程调用，比内存快照贵得多）。</summary>
    private static readonly TimeSpan NotificationThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>等待空间释放时的复查间隔。</summary>
    private static readonly TimeSpan StorageRecheckInterval = TimeSpan.FromSeconds(3);

    private readonly Context _context;
    private readonly ImageDownloader _downloader;
    private readonly AndroidNetworkMonitor _network;
    private readonly DownloadNotifier _notifier;
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly string _stagingDirectory;

    private ItemState[] _items = Array.Empty<ItemState>();
    private DownloadBatchState _batchState = DownloadBatchState.Idle;
    private string? _batchMessage;
    private DownloadBatchSnapshot _snapshot = DownloadBatchSnapshot.Idle;
    private DownloadBatchResult? _lastResult;
    private CancellationTokenSource? _activeBatch;

    private long _lastSnapshotTicks;
    private long _lastNotificationTicks;
    private bool _disposed;

    /// <summary>
    /// 创建模块。
    /// </summary>
    /// <param name="context">
    /// 上下文。模块内部会取 <c>ApplicationContext</c> —— 传 Activity 进来也不会泄漏。
    /// </param>
    /// <param name="downloader">
    /// Core 的下载器。<b>建议与画廊缩略图共用同一个实例</b>：并发闸门是它的实例状态，
    /// 共用一个实例才能让「缩略图 + 批量保存」共享同一份并发预算，而不是各开一倍的连接。
    /// </param>
    /// <param name="launcherActivity">点通知要拉起的界面类型；为 null 时通知不带跳转。</param>
    public AndroidDownloadModule(
        Context context,
        ImageDownloader downloader,
        Type? launcherActivity = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(downloader);

        _context = context.ApplicationContext ?? context;
        _downloader = downloader;
        _network = new AndroidNetworkMonitor(_context);
        _notifier = new DownloadNotifier(_context, launcherActivity);
        // 服务与模块共用同一个通知 id，服务需要知道点通知该拉起谁。
        DownloadForegroundService.LauncherActivity = launcherActivity;

        // 中转文件放缓存目录：这里是私有空间，不受分区存储约束，
        // 也让「下载失败」永远不会在用户相册里留下半成品。
        var cacheRoot = _context.CacheDir?.AbsolutePath
            ?? Path.GetTempPath();
        _stagingDirectory = Path.Combine(cacheRoot, "download-staging");
    }

    /// <inheritdoc />
    public DownloadBatchSnapshot Current
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot;
            }
        }
    }

    /// <inheritdoc />
    public DownloadBatchResult? LastResult
    {
        get
        {
            lock (_stateLock)
            {
                return _lastResult;
            }
        }
    }

    /// <inheritdoc />
    public bool IsBusy => _batchGate.CurrentCount == 0;

    /// <inheritdoc />
    public event EventHandler<DownloadBatchSnapshot>? SnapshotChanged;

    /// <inheritdoc />
    public async Task<DownloadBatchResult> EnqueueAsync(
        DownloadBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 只允许一批在跑：两批同时跑会让「当前进度」这个单一概念失去意义，
        // 也会把前台服务通知的语义搞乱。界面在下载期间本来就会禁用入口。
        if (!await _batchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有批量下载正在进行中。");
        }

        try
        {
            return await RunBatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                _activeBatch = null;
                _items = Array.Empty<ItemState>();
            }

            _batchGate.Release();
        }
    }

    /// <inheritdoc />
    public void CancelActive()
    {
        CancellationTokenSource? active;
        lock (_stateLock)
        {
            active = _activeBatch;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 批次刚好结束并释放了令牌：取消是空操作。
        }
    }

    private async Task<DownloadBatchResult> RunBatchAsync(
        DownloadBatchRequest request,
        CancellationToken cancellationToken)
    {
        var options = request.EffectiveOptions;
        if (request.Items.Count == 0)
        {
            return DownloadBatchResult.Empty;
        }

        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock)
        {
            _activeBatch = batch;
            _items = BuildItems(request);
            _batchState = DownloadBatchState.Preparing;
            _batchMessage = "准备中…";
            _lastSnapshotTicks = 0;
            _lastNotificationTicks = 0;
        }

        Publish(force: true);

        var token = batch.Token;
        var target = await PrepareTargetAsync(options, token).ConfigureAwait(false);
        if (target is null)
        {
            return Finish(DownloadBatchState.Failed, "无法确定下载位置", options);
        }

        if (!await WaitForStorageAsync(target, options, token).ConfigureAwait(false))
        {
            var message = Current.Message ?? "可用空间不足";
            return Finish(token.IsCancellationRequested ? DownloadBatchState.Cancelled : DownloadBatchState.Failed, message, options);
        }

        if (!await WaitForNetworkAsync(options, token).ConfigureAwait(false))
        {
            var message = token.IsCancellationRequested ? "已取消" : "等待网络超时";
            return Finish(token.IsCancellationRequested ? DownloadBatchState.Cancelled : DownloadBatchState.Failed, message, options);
        }

        // 从这里开始是「真在传输」的区间：需要前台服务把进程抬到前台，并借它挂住唤醒锁。
        DownloadForegroundService.BeginKeepAlive(_context);
        try
        {
            await RunPassesAsync(target, options, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是正常路径：下面统一把未完成的项标记为已取消。
        }
        finally
        {
            DownloadForegroundService.EndKeepAlive(_context);
        }

        var cancelled = token.IsCancellationRequested;
        MarkUnfinishedAsCancelled();
        var state = cancelled
            ? DownloadBatchState.Cancelled
            : _items.Any(i => i.State == DownloadItemState.Failed)
                ? DownloadBatchState.Failed
                : DownloadBatchState.Completed;
        return Finish(state, null, options, target.DisplayLocation);
    }

    private static ItemState[] BuildItems(DownloadBatchRequest request)
    {
        var specs = DownloadPlanner.Plan(request.Items, request.EffectiveOptions.AlbumName);
        var items = new ItemState[specs.Count];
        for (var i = 0; i < specs.Count; i++)
        {
            items[i] = new ItemState(i, request.Items[i], specs[i]);
        }

        return items;
    }

    private async Task<IDownloadTarget?> PrepareTargetAsync(DownloadBatchOptions options, CancellationToken token)
    {
        try
        {
            var probe = AndroidDownloadTargets.Create(_context, options);
            await probe.GetAvailableBytesAsync(token).ConfigureAwait(false);
            return probe;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>空间预检：不足时进入「等待空间」，每 3 秒复查一次，超时即整批失败。</summary>
    private async Task<bool> WaitForStorageAsync(
        IDownloadTarget target,
        DownloadBatchOptions options,
        CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + options.NetworkWaitTimeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var free = await target.GetAvailableBytesAsync(token).ConfigureAwait(false);
            var issue = DownloadPolicy.CheckStorage(free, null, options.MinimumFreeBytes);
            if (issue is null)
            {
                return true;
            }

            SetState(DownloadBatchState.WaitingForStorage, issue, force: true);
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(StorageRecheckInterval, token).ConfigureAwait(false);
        }
    }

    /// <summary>网络准入：不满足时进入「等待网络」，由系统回调唤醒。</summary>
    private async Task<bool> WaitForNetworkAsync(DownloadBatchOptions options, CancellationToken token)
    {
        _network.Start();
        return await _network
            .WaitUntilAllowedAsync(
                options.WifiOnly,
                options.NetworkWaitTimeout,
                kind => SetState(
                    DownloadBatchState.WaitingForNetwork,
                    DownloadPolicy.DescribeNetwork(kind, options.WifiOnly),
                    force: true),
                token)
            .ConfigureAwait(false);
    }

    /// <summary>按「主跑一遍 + 收尾补跑」执行。</summary>
    private async Task RunPassesAsync(IDownloadTarget target, DownloadBatchOptions options, CancellationToken token)
    {
        var profile = new DeviceProfile(
            System.Environment.ProcessorCount,
            ReadMemoryClass(),
            IsLowRamDevice());
        var concurrency = DownloadPolicy.ResolveConcurrency(options.MaxConcurrentDownloads, profile);
        var passes = Math.Max(1, 1 + Math.Max(0, options.RetryCount));

        for (var pass = 0; pass < passes; pass++)
        {
            token.ThrowIfCancellationRequested();
            var pending = _items
                .Where(i => i.State == DownloadItemState.Queued
                    || (pass > 0 && i.State == DownloadItemState.Failed))
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            if (pass > 0)
            {
                // 补跑前清掉上一轮的失败标记，否则这些项在快照里会一直显示为「失败」。
                foreach (var item in pending)
                {
                    item.ResetForRetry();
                }

                SetState(DownloadBatchState.Running, $"重试 {pending.Length} 张…", force: true);
            }
            else
            {
                SetState(DownloadBatchState.Running, null, force: true);
            }

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = token,
                },
                // 显式写成 async 包装：Parallel.ForEachAsync 的委托要求返回 ValueTask，
                // 而 DownloadItemAsync 返回 Task —— 非 async 的表达式 lambda 不会做这个转换。
                async (item, itemToken) => await DownloadItemAsync(item, target, options, itemToken)
                    .ConfigureAwait(false))
                .ConfigureAwait(false);
        }
    }

    private async Task DownloadItemAsync(
        ItemState item,
        IDownloadTarget target,
        DownloadBatchOptions options,
        CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested)
            {
                TransitionTo(item, DownloadItemState.Cancelled);
                return;
            }

            if (!options.OverwriteExisting && await target.ExistsAsync(item.Spec, token).ConfigureAwait(false))
            {
                TransitionTo(item, DownloadItemState.Skipped);
                return;
            }

            TransitionTo(item, DownloadItemState.Running);
            var staging = Path.Combine(_stagingDirectory, $"{item.Index:D4}{Path.GetExtension(item.Spec.DisplayName)}");
            try
            {
                // 传输层：Core 负责并发闸门 / 退避重试 / .part + 原子改名。
                var progress = new Progress<DownloadProgress>(p => OnItemProgress(item, p));
                await _downloader
                    .DownloadToFileAsync(item.Item.Url, staging, progress, token)
                    .ConfigureAwait(false);

                // 落盘层：模块负责目标存储（MediaStore / 私有目录）与原子提交。
                await using var content = new FileStream(
                    staging,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    useAsync: true);
                var location = await target.WriteAsync(item.Spec, content, token).ConfigureAwait(false);
                item.Location = location;
                TransitionTo(item, DownloadItemState.Completed);
            }
            finally
            {
                TryDeleteFile(staging);
            }
        }
        catch (OperationCanceledException)
        {
            TransitionTo(item, DownloadItemState.Cancelled);
        }
        catch (Exception ex)
        {
            item.Error = DownloadPolicy.DescribeFailure(ex, token);
            TransitionTo(item, DownloadItemState.Failed);
        }
    }

    private void OnItemProgress(ItemState item, DownloadProgress progress)
    {
        item.ReceivedBytes = progress.DownloadedBytes;
        item.TotalBytes = progress.TotalBytes;
        Publish(force: false);
    }

    // ---------------- 状态与快照 ----------------

    private void MarkUnfinishedAsCancelled()
    {
        foreach (var item in _items)
        {
            if (item.State is DownloadItemState.Queued or DownloadItemState.Running)
            {
                TransitionTo(item, DownloadItemState.Cancelled);
            }
        }
    }

    private void TransitionTo(ItemState item, DownloadItemState state)
    {
        item.State = state;
        if (state is DownloadItemState.Completed or DownloadItemState.Skipped)
        {
            item.Error = null;
        }

        if (state == DownloadItemState.Skipped)
        {
            // 跳过时没有产生流量：把字节数归零，否则整批的字节统计会虚高。
            item.ReceivedBytes = 0;
            item.TotalBytes = 0;
        }

        Publish(force: true);
    }

    private void SetState(DownloadBatchState state, string? message, bool force)
    {
        lock (_stateLock)
        {
            _batchState = state;
            _batchMessage = message;
        }

        Publish(force);
    }

    /// <summary>构造并发布一份新的不可变快照。节流规则见类注释。</summary>
    private void Publish(bool force)
    {
        DownloadBatchSnapshot snapshot;
        var now = System.Environment.TickCount64;
        lock (_stateLock)
        {
            if (!force && now - _lastSnapshotTicks < SnapshotThrottle.TotalMilliseconds)
            {
                return;
            }

            _lastSnapshotTicks = now;
            snapshot = _snapshot = BuildSnapshot();
        }

        SnapshotChanged?.Invoke(this, snapshot);

        // 通知栏比内存快照贵得多，单独再节流一次。
        if (force || now - _lastNotificationTicks >= NotificationThrottle.TotalMilliseconds)
        {
            _lastNotificationTicks = now;
            _notifier.ShowProgress(snapshot);
        }
    }

    private DownloadBatchSnapshot BuildSnapshot()
    {
        var itemSnapshots = new DownloadItemSnapshot[_items.Length];
        long received = 0;
        long? total = 0;
        var completed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var item in _items)
        {
            itemSnapshots[item.Index] = item.ToSnapshot();
            received += item.ReceivedBytes;
            total = total is null || item.TotalBytes is null ? null : total + item.TotalBytes;
            switch (item.State)
            {
                case DownloadItemState.Completed:
                    completed++;
                    break;
                case DownloadItemState.Failed:
                    failed++;
                    break;
                case DownloadItemState.Skipped:
                    skipped++;
                    break;
                default:
                    break;
            }
        }

        return new DownloadBatchSnapshot(
            _batchState,
            _items.Length,
            completed,
            failed,
            skipped,
            received,
            total,
            _batchMessage,
            itemSnapshots);
    }

    private DownloadBatchResult Finish(
        DownloadBatchState state,
        string? message,
        DownloadBatchOptions options,
        string location = "")
    {
        SetState(state, message, force: true);

        var locations = new List<string>();
        var errors = new List<string>();
        var failureIndices = new List<int>();
        var completed = 0;
        var skipped = 0;
        foreach (var item in _items)
        {
            switch (item.State)
            {
                case DownloadItemState.Completed:
                    completed++;
                    if (item.Location is not null)
                    {
                        locations.Add(item.Location);
                    }

                    break;
                case DownloadItemState.Skipped:
                    skipped++;
                    break;
                case DownloadItemState.Failed:
                    errors.Add($"{item.Spec.DisplayName}：{item.Error ?? "未知原因"}");
                    failureIndices.Add(item.Index);
                    break;
                default:
                    break;
            }
        }

        var result = new DownloadBatchResult(
            completed,
            skipped,
            errors.Count,
            state == DownloadBatchState.Cancelled,
            location,
            locations,
            errors,
            failureIndices);

        lock (_stateLock)
        {
            _lastResult = result;
        }

        _notifier.ClearProgress();
        _notifier.ShowResult(result, location, options.NotifyOnCompletion);
        return result;
    }

    private int ReadMemoryClass()
    {
        try
        {
            return (_context.GetSystemService(Context.ActivityService) as ActivityManager)?.MemoryClass ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private bool IsLowRamDevice()
    {
        try
        {
            return (_context.GetSystemService(Context.ActivityService) as ActivityManager)?.IsLowRamDevice ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 缓存目录里的残留文件由系统在空间紧张时回收，这里失败无需处理。
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
        CancelActive();
        _network.Dispose();
        _notifier.Dispose();
        _batchGate.Dispose();
    }

    /// <summary>运行期可变的逐项状态；对外只以不可变 <see cref="DownloadItemSnapshot"/> 暴露。</summary>
    private sealed class ItemState(int index, ImageItem item, DownloadFileSpec spec)
    {
        public int Index { get; } = index;

        public ImageItem Item { get; } = item;

        public DownloadFileSpec Spec { get; } = spec;

        public DownloadItemState State { get; set; } = DownloadItemState.Queued;

        public long ReceivedBytes { get; set; }

        public long? TotalBytes { get; set; }

        public string? Error { get; set; }

        public string? Location { get; set; }

        /// <summary>补跑前复位：清掉上一轮的字节数与失败原因，但保留文件名规划。</summary>
        public void ResetForRetry()
        {
            State = DownloadItemState.Queued;
            ReceivedBytes = 0;
            Error = null;
        }

        public DownloadItemSnapshot ToSnapshot() => new(
            Index,
            Spec.DisplayName,
            Item.Url,
            State,
            ReceivedBytes,
            TotalBytes,
            Error,
            Location);
    }
}
