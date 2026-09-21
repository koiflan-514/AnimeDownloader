namespace AnimeDownloader.Download.Contracts;

/// <summary>
/// 下载模块的对外唯一入口。
/// </summary>
/// <remarks>
/// <para><b>职责边界</b> —— 模块负责：把一批已经解析好的图片条目可靠地落到设备存储上，
/// 并在 Android 的后台限制下完成这件事（前台服务保活、通知进度、网络与空间策略、
/// 并发与重试、取消与失败归因）。</para>
/// <para>模块<b>不</b>负责：图源解析（<c>Core.Sources</c>）、图片解码与呈现、
/// 界面渲染 —— 它只对外暴露不可变快照与命令。</para>
/// <para><b>线程约定</b>：<see cref="SnapshotChanged"/> 在<b>后台线程</b>触发，
/// 订阅方自行切回 UI 线程；快照本身是不可变的，跨线程读取安全。</para>
/// </remarks>
public interface IDownloadModule : IDisposable
{
    /// <summary>当前批次的快照；没有批次时为 <see cref="DownloadBatchSnapshot.Idle"/>。</summary>
    DownloadBatchSnapshot Current { get; }

    /// <summary>上一次结束的批次结果；从未跑过时为 null。</summary>
    DownloadBatchResult? LastResult { get; }

    /// <summary>是否有批次正在进行。界面据此切换「下载全部 / 取消」按钮。</summary>
    bool IsBusy { get; }

    /// <summary>快照发生变化（进度、状态、等待网络……）时触发。在后台线程触发。</summary>
    event EventHandler<DownloadBatchSnapshot>? SnapshotChanged;

    /// <summary>
    /// 开始一批下载并等待其结束。
    /// </summary>
    /// <param name="request">待下载条目与本批策略。</param>
    /// <param name="cancellationToken">取消整批（已完成的项不会被回滚）。</param>
    /// <exception cref="InvalidOperationException">已有批次在进行中。</exception>
    Task<DownloadBatchResult> EnqueueAsync(
        DownloadBatchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>取消当前批次；没有批次时为空操作。</summary>
    void CancelActive();
}
