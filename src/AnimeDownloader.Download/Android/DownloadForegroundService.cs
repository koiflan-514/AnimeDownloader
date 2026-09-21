using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AnimeDownloader.Download.Contracts;

namespace AnimeDownloader.Download;

/// <summary>
/// 下载保活用的前台服务。
/// </summary>
/// <remarks>
/// <para><b>它做什么</b>：把进程抬到前台（<c>startForeground</c>），并在传输期间持有一把
/// <c>PARTIAL_WAKE_LOCK</c>，让屏幕熄灭后 <b>CPU 不停摆</b>。用户切到别的应用、
/// 按电源键锁屏，批量下载都继续跑。</para>
///
/// <para><b>它不做什么</b>：<b>不跑下载逻辑</b>。真正的工作在 <see cref="AndroidDownloadModule"/> 里，
/// 与 UI 同进程。这样切前台/后台不需要跨进程传数据，进度回调也不必序列化 ——
/// 服务只承担「保活 + 挂唤醒锁 + 提供那条不可少的通知」这三件系统强制的事。</para>
///
/// <para><b>为什么不直接 <c>StartForegroundService</c> 然后不管</b>：API 26+ 规定
/// <c>startForegroundService</c> 之后 5 秒内必须调用 <c>startForeground</c>，否则系统直接
/// 抛 <c>ForegroundServiceDidNotStartInTimeException</c> 并杀进程。所以通知不是「顺便显示一下」，
/// 而是前台服务的生存前提。通知 id 与模块共用，模块随后的 <c>notify</c> 就是在这条通知上刷新进度。</para>
/// </remarks>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class DownloadForegroundService : Service
{
    private const string ActionBegin = "com.koiflan.animedownloader.action.DOWNLOAD_BEGIN";
    private const string ActionEnd = "com.koiflan.animedownloader.action.DOWNLOAD_END";

    /// <summary>唤醒锁的最长持有时间。到期自动释放，避免异常路径下把电池耗干。</summary>
    private static readonly TimeSpan WakeLockTimeout = TimeSpan.FromMinutes(30);

    /// <summary>点前台通知要拉起的界面。由 <see cref="AndroidDownloadModule"/> 在构造时写入。</summary>
    internal static Type? LauncherActivity { get; set; }

    private PowerManager.WakeLock? _wakeLock;
    private DownloadNotifier? _notifier;
    private bool _foreground;

    /// <summary>请求开始保活。已在前台时是幂等的。</summary>
    internal static void BeginKeepAlive(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(DownloadForegroundService));
            intent.SetAction(ActionBegin);
            // StartForegroundService 是 API 26 才有的。24/25 上直接 StartService 即可 ——
            // 那两版还没有「前台服务必须 5 秒内 startForeground」的硬性规定。
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception)
        {
            // 起不来也不该让下载失败：前台服务的价值是「更不容易被杀」，
            // 没有它下载仍然能在进程存活期间正常完成。
        }
    }

    /// <summary>结束保活。模块在批次收尾时调用。</summary>
    internal static void EndKeepAlive(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(DownloadForegroundService));
            intent.SetAction(ActionEnd);
            context.StartService(intent);
        }
        catch (Exception)
        {
            // 同上：清理失败不应影响下载结果。
        }
    }

    /// <inheritdoc />
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionEnd)
        {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        EnterForeground();
        AcquireWakeLock();
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc />
    public override IBinder? OnBind(Intent? intent) => null;

    /// <inheritdoc />
    public override void OnDestroy()
    {
        ReleaseWakeLock();
        if (_foreground)
        {
            StopForeground(StopForegroundFlags.Remove);
            _foreground = false;
        }

        base.OnDestroy();
    }

    private void EnterForeground()
    {
        if (_foreground)
        {
            return;
        }

        _notifier ??= new DownloadNotifier(this, LauncherActivity);
        _notifier.EnsureChannels();

        // 先立一条「准备中」的通知占位：它随后会被模块的进度刷新覆盖（同一个 id）。
        var placeholder = _notifier.BuildProgress(
            DownloadBatchSnapshot.Idle with
            {
                State = DownloadBatchState.Preparing,
                Message = "准备下载…",
            });
        if (placeholder is null)
        {
            return;
        }

        StartForeground(DownloadNotifier.ProgressNotificationId, placeholder);
        _foreground = true;
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock is { IsHeld: true })
        {
            return;
        }

        try
        {
            var manager = GetSystemService(PowerService) as PowerManager;
            var lock_ = manager?.NewWakeLock(WakeLockFlags.Partial, "AnimeDownloader:downloads");
            lock_?.Acquire((long)WakeLockTimeout.TotalMilliseconds);
            _wakeLock = lock_;
        }
        catch (Exception)
        {
            // 拿不到唤醒锁（缺 WAKE_LOCK 权限等）：退化为「前台但可被 Doze 限流」。
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is { IsHeld: true })
            {
                _wakeLock.Release();
            }
        }
        catch (Exception)
        {
            // 释放失败无需处理。
        }
        finally
        {
            _wakeLock = null;
        }
    }
}
