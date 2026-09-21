using Android.App;
using Android.Content;
using AnimeDownloader.Download.Contracts;

namespace AnimeDownloader.Download;

/// <summary>
/// 下载通知的呈现层：一条常驻的进度通知 + 一条结束时的结果通知。
/// </summary>
/// <remarks>
/// <para>两个设计约束来自 Android 本身，而不是风格偏好：</para>
/// <list type="number">
///   <item><description><b>前台服务必须有一条通知</b>，否则系统在 5 秒内直接杀进程（API 26+）。
///   所以这条通知是「保活」的载体，而不是可选的装饰。</description></item>
///   <item><description><b>进度通知必须安静</b>：渠道重要性定为 Low、并置 <c>SetOnlyAlertOnce</c>。
///   否则每刷新一次进度就响一声 —— 这是下载器最经典的翻车方式。</description></item>
/// </list>
/// <para>通知栏不显示应用自己画的图标以外的任何东西：小图标是单色剪影（系统要求），
/// 由模块自带的 <c>Resources/drawable/ic_download.xml</c> 提供，随库资源合并进应用。</para>
/// </remarks>
internal sealed class DownloadNotifier : IDisposable
{
    /// <summary>进度通知的 id（常驻，随批次结束撤销）。</summary>
    internal const int ProgressNotificationId = 0x4144;

    /// <summary>结果通知的 id（可关闭）。</summary>
    internal const int ResultNotificationId = 0x4145;

    private const string ProgressChannelId = "anime_downloads_active";
    private const string ResultChannelId = "anime_downloads_result";

    private readonly Context _context;
    private readonly NotificationManager? _manager;
    private readonly Type? _launcherActivity;
    private readonly int _smallIcon;
    private bool _channelsReady;

    /// <param name="context">上下文（用 Application 上下文，避免持有 Activity 导致泄漏）。</param>
    /// <param name="launcherActivity">点通知要拉起的界面；为 null 时通知不带跳转。</param>
    internal DownloadNotifier(Context context, Type? launcherActivity = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _launcherActivity = launcherActivity;
        _manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        // 通知的小图标必须是**单色剪影**，且必须来自本应用自己的资源 —— 用启动图标（彩色）
        // 会被系统渲染成一坨白块。这里用模块自带的矢量图标（Resources/drawable/ic_download.xml）。
        _smallIcon = Resource.Drawable.ic_download;
    }

    /// <summary>系统是否允许我们发通知（API 33+ 需要用户授权，未授权时静默降级）。</summary>
    internal bool CanNotify
    {
        get
        {
            if (_manager is null)
            {
                return false;
            }

            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                return true;
            }

            return _context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
                == global::Android.Content.PM.Permission.Granted;
        }
    }

    /// <summary>创建通知渠道（API 26+）。重复创建同名渠道是幂等的，系统会忽略后续的定义。</summary>
    internal void EnsureChannels()
    {
        if (_channelsReady || _manager is null || !OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            _channelsReady = true;
            return;
        }

        var active = new NotificationChannel(
            ProgressChannelId,
            "下载进度",
            NotificationImportance.Low)
        {
            Description = "批量下载时的进度提示（静默）",
        };
        active.SetShowBadge(false);

        var result = new NotificationChannel(
            ResultChannelId,
            "下载结果",
            NotificationImportance.Default)
        {
            Description = "批量下载完成或失败后的提示",
        };

        _manager.CreateNotificationChannel(active);
        _manager.CreateNotificationChannel(result);
        _channelsReady = true;
    }

    /// <summary>
    /// 构建进度通知。返回 null 表示当前不需要/不允许显示（无通知权限）。
    /// </summary>
    internal Notification? BuildProgress(DownloadBatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!CanNotify)
        {
            return null;
        }

        EnsureChannels();
        var builder = CreateBuilder(ProgressChannelId);
        builder.SetSmallIcon(_smallIcon);
        builder.SetContentTitle("正在下载");
        builder.SetContentText(DescribeProgress(snapshot));
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);

        // 前台服务的通知不可滑动清除，但用户可以点进来看。
        builder.SetContentIntent(BuildContentIntent());

        if (snapshot.State == DownloadBatchState.WaitingForNetwork
            || snapshot.State == DownloadBatchState.WaitingForStorage
            || snapshot.State == DownloadBatchState.Preparing)
        {
            // 等待态用不确定进度条：此时「百分比」没有意义，画一条定值进度条只会骗人。
            builder.SetProgress(0, 0, true);
        }
        else
        {
            var max = Math.Max(1, snapshot.Total);
            builder.SetProgress(max, snapshot.Finished, false);
        }

        return builder.Build();
    }

    /// <summary>推进度通知。失败静默 —— 通知发不出去不应该影响下载本身。</summary>
    internal void ShowProgress(DownloadBatchSnapshot snapshot)
    {
        var notification = BuildProgress(snapshot);
        if (notification is null || _manager is null)
        {
            return;
        }

        try
        {
            _manager.Notify(ProgressNotificationId, notification);
        }
        catch (Exception)
        {
            // 忽略：部分定制系统在通知被用户关闭后会抛异常。
        }
    }

    /// <summary>撤掉进度通知。</summary>
    internal void ClearProgress()
    {
        try
        {
            _manager?.Cancel(ProgressNotificationId);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    /// <summary>显示批次结束的结果通知。</summary>
    internal void ShowResult(DownloadBatchResult result, string location, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!enabled || !CanNotify || _manager is null)
        {
            return;
        }

        EnsureChannels();
        var builder = CreateBuilder(ResultChannelId);
        builder.SetSmallIcon(_smallIcon);
        builder.SetAutoCancel(true);
        builder.SetContentIntent(BuildContentIntent());
        builder.SetContentTitle(result.Cancelled ? "下载已取消" : result.Failed == 0 ? "下载完成" : "下载结束");
        builder.SetContentText(DescribeResult(result, location));
        if (result.Errors.Count > 0)
        {
            builder.SetStyle(new Notification.BigTextStyle().BigText(DescribeResultDetail(result, location)));
        }

        try
        {
            _manager.Notify(ResultNotificationId, builder.Build());
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    /// <summary>进度通知里那行字：优先说明「为什么没在跑」。</summary>
    internal static string DescribeProgress(DownloadBatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.State is DownloadBatchState.WaitingForNetwork or DownloadBatchState.WaitingForStorage)
        {
            return snapshot.Message ?? "等待中…";
        }

        if (snapshot.State == DownloadBatchState.Preparing)
        {
            return snapshot.Message ?? "准备中…";
        }

        var aggregate = snapshot.TotalBytes is > 0
            ? $"{DownloadPolicy.FormatBytes(snapshot.ReceivedBytes)} / {DownloadPolicy.FormatBytes(snapshot.TotalBytes.Value)}"
            : DownloadPolicy.FormatBytes(snapshot.ReceivedBytes);
        return $"{snapshot.Finished}/{snapshot.Total} · {aggregate}";
    }

    /// <summary>结果通知的一行摘要。</summary>
    internal static string DescribeResult(DownloadBatchResult result, string location)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Cancelled)
        {
            return $"已取消 · 已保存 {result.Succeeded} 张";
        }

        var skipped = result.Skipped > 0 ? $"，跳过 {result.Skipped} 张（已存在）" : string.Empty;
        return result.Failed == 0
            ? $"已保存 {result.Succeeded} 张到 {location}{skipped}"
            : $"成功 {result.Succeeded} 张，失败 {result.Failed} 张{skipped}";
    }

    /// <summary>结果通知展开后的详情：逐条失败原因（最多 5 条，避免通知被撑爆）。</summary>
    internal static string DescribeResultDetail(DownloadBatchResult result, string location)
    {
        ArgumentNullException.ThrowIfNull(result);

        var head = $"{DescribeResult(result, location)}\n";
        if (result.Errors.Count == 0)
        {
            return head;
        }

        var lines = result.Errors.Take(5).Select(e => "· " + e);
        var more = result.Errors.Count > 5 ? $"\n…另有 {result.Errors.Count - 5} 条失败" : string.Empty;
        return head + string.Join('\n', lines) + more;
    }

    private Notification.Builder CreateBuilder(string channelId)
    {
        // 24/25 没有渠道概念，构造器也要求不同；26+ 必须带渠道 id。
        return OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(_context, channelId)
            : new Notification.Builder(_context);
    }

    /// <summary>点通知回到应用。</summary>
    private PendingIntent? BuildContentIntent()
    {
        if (_launcherActivity is null)
        {
            return null;
        }

        var intent = new Intent(_context, _launcherActivity);
        intent.AddFlags(ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(
            _context,
            0,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    /// <inheritdoc />
    public void Dispose() => ClearProgress();
}
