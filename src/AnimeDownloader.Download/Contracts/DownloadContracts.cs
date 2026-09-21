using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Download.Contracts;

/// <summary>
/// 单张图片在一次批量下载中的状态。
/// </summary>
/// <remarks>
/// 六态是**互斥终态 + 一个过程态**：<see cref="Queued"/> 与 <see cref="Running"/> 是过程态，
/// 其余四个都是终态。批量结束时每一项都必须落在终态上 —— 这样「已完成 + 失败 + 已跳过 + 已取消」
/// 恒等于总数，进度条不会卡在 99%。
/// </remarks>
public enum DownloadItemState
{
    /// <summary>已排队，等待并发闸门。</summary>
    Queued,

    /// <summary>正在传输。</summary>
    Running,

    /// <summary>字节已完整写入目标并提交。</summary>
    Completed,

    /// <summary>目标位置已存在同名且非空的文件，本次按策略跳过。</summary>
    Skipped,

    /// <summary>失败（网络、HTTP 状态、落盘错误等），<c>Error</c> 中带可读原因。</summary>
    Failed,

    /// <summary>被用户取消，或所在批次被取消。</summary>
    Cancelled,
}

/// <summary>整批下载的状态。</summary>
public enum DownloadBatchState
{
    /// <summary>没有正在进行的批次。</summary>
    Idle,

    /// <summary>正在规划落点与预检空间。</summary>
    Preparing,

    /// <summary>当前网络不满足策略（如开启了「仅 Wi‑Fi」而设备在用移动数据），等待中。</summary>
    WaitingForNetwork,

    /// <summary>可用空间低于阈值，等待用户清理。</summary>
    WaitingForStorage,

    /// <summary>传输中。</summary>
    Running,

    /// <summary>全部结束，且至少有一项失败。</summary>
    Failed,

    /// <summary>全部成功（含跳过）。</summary>
    Completed,

    /// <summary>被取消。</summary>
    Cancelled,
}

/// <summary>单张图片的传输进度。</summary>
/// <param name="ReceivedBytes">已接收字节数。</param>
/// <param name="TotalBytes">服务器声明的总长度；未知时为 null。</param>
public readonly record struct DownloadItemProgress(long ReceivedBytes, long? TotalBytes)
{
    /// <summary>完成比例（0–1）；总长度未知时为 null，界面据此显示不确定进度。</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// 单张图片在某一时刻的不可变快照。UI 通过它渲染列表，不需要回头读模块内部状态。
/// </summary>
/// <param name="Index">在批次中的下标（与请求顺序一致）。</param>
/// <param name="DisplayName">最终落盘文件名（经净化与去重），包含扩展名。</param>
/// <param name="Url">源地址，用于失败后重试。</param>
/// <param name="State">当前状态。</param>
/// <param name="ReceivedBytes">已写入目标的字节数。</param>
/// <param name="TotalBytes">预期总字节数；未知时为 null。</param>
/// <param name="Error">失败原因（仅 <see cref="DownloadItemState.Failed"/> 非空）。</param>
/// <param name="Location">成功后的落点（Content URI 或绝对路径），未完成时为 null。</param>
public sealed record DownloadItemSnapshot(
    int Index,
    string DisplayName,
    string Url,
    DownloadItemState State,
    long ReceivedBytes,
    long? TotalBytes,
    string? Error = null,
    string? Location = null)
{
    /// <summary>该张的完成比例；总长度未知时为 null。</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;

    /// <summary>是否为终态（不会再变化）。</summary>
    public bool IsFinished => State is DownloadItemState.Completed
        or DownloadItemState.Skipped
        or DownloadItemState.Failed
        or DownloadItemState.Cancelled;
}

/// <summary>
/// 整批下载在某一时刻的不可变快照。这是界面与通知唯一的读取来源。
/// </summary>
/// <param name="State">批次状态。</param>
/// <param name="Total">本批总数。</param>
/// <param name="Completed">已成功写入的数量。</param>
/// <param name="Failed">失败数量。</param>
/// <param name="Skipped">因已存在而跳过的数量。</param>
/// <param name="ReceivedBytes">本批已写入的总字节数。</param>
/// <param name="TotalBytes">本批预期总字节数；全部未知时为 null。</param>
/// <param name="Message">给用户看的一句话状态（等待网络、空间不足等）。</param>
/// <param name="Items">逐项快照，下标与请求顺序一致。</param>
public sealed record DownloadBatchSnapshot(
    DownloadBatchState State,
    int Total,
    int Completed,
    int Failed,
    int Skipped,
    long ReceivedBytes,
    long? TotalBytes,
    string? Message,
    IReadOnlyList<DownloadItemSnapshot> Items)
{
    /// <summary>没有任何批次在跑时的空快照。</summary>
    public static DownloadBatchSnapshot Idle { get; } = new(
        DownloadBatchState.Idle, 0, 0, 0, 0, 0, null, null, Array.Empty<DownloadItemSnapshot>());

    /// <summary>已落到终态的数量（成功 + 失败 + 跳过 + 取消）。</summary>
    public int Finished =>
        Items.Count(i => i.IsFinished);

    /// <summary>整批完成比例（0–1）。用<b>项数</b>而不是字节数 —— 各图大小悬殊，字节口径会让进度长时间停在个位数。</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Finished / Total, 0, 1) : 0;

    /// <summary>仍在传输中的数量。</summary>
    public int Running => Items.Count(i => i.State == DownloadItemState.Running);

    /// <summary>是否仍有未到终态的项。</summary>
    public bool HasPending => Finished < Items.Count;
}

/// <summary>批量下载的最终结果。终态快照的浓缩版，便于调用方只关心结果时使用。</summary>
/// <param name="Succeeded">成功写入的数量。</param>
/// <param name="Skipped">因已存在而跳过的数量。</param>
/// <param name="Failed">失败数量。</param>
/// <param name="Cancelled">是否被取消（取消时前四项仍反映已完成的真实进展）。</param>
/// <param name="Location">落点说明（如「相册 / AnimeDownloader」），用于结果提示。</param>
/// <param name="Locations">成功项的落点，按完成顺序排列。</param>
/// <param name="Errors">失败原因，形如 <c>文件名：原因</c>。</param>
/// <param name="FailureIndices">失败项的下标，供「仅重试失败项」使用。</param>
public sealed record DownloadBatchResult(
    int Succeeded,
    int Skipped,
    int Failed,
    bool Cancelled,
    string Location,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Errors,
    IReadOnlyList<int> FailureIndices)
{
    /// <summary>落空的结果（批次还没跑就结束）。</summary>
    public static DownloadBatchResult Empty { get; } = new(
        0, 0, 0, false, string.Empty, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>());

    /// <summary>成功 + 跳过 == 总数，且没有失败也没有取消。</summary>
    public bool IsClean => Failed == 0 && !Cancelled;
}

/// <summary>一次批量下载请求。模块的唯一入参。</summary>
/// <param name="Items">待下载的图片条目（来自 <c>Core.Sources</c>，模块本身不拉列表）。</param>
/// <param name="Options">本批策略；为 null 时使用默认策略。</param>
public sealed record DownloadBatchRequest(
    IReadOnlyList<ImageItem> Items,
    DownloadBatchOptions? Options = null)
{
    /// <summary>本批策略（null 安全）。</summary>
    public DownloadBatchOptions EffectiveOptions => Options ?? new DownloadBatchOptions();
}

/// <summary>
/// 落点选择。两条路在权限、可见性与卸载行为上完全不同，因此必须由用户显式选。
/// </summary>
public enum DownloadLocation
{
    /// <summary>
    /// 系统相册（<c>Pictures/&lt;相册名&gt;</c>）。
    /// Android 10+ 走 MediaStore，<b>零权限</b>，文件立刻出现在系统相册与其它应用的选图器里，
    /// 卸载应用不会被删除。Android 9 及以下需要 <c>WRITE_EXTERNAL_STORAGE</c>。
    /// </summary>
    Gallery,

    /// <summary>
    /// 应用私有外部目录（<c>Android/data/&lt;包名&gt;/files/Pictures/&lt;相册名&gt;</c>）。
    /// 任何版本都<b>不需要权限</b>，不进系统相册，但卸载应用时会被一并删除。
    /// </summary>
    AppPrivate,
}

/// <summary>
/// 一批下载的策略。全部字段都有默认值 —— 默认值即「手机上的保守选择」：
/// 不限制网络但并发压低、空间不足 64MB 就停、目标相册名为 AnimeDownloader。
/// </summary>
public sealed record DownloadBatchOptions
{
    /// <summary>系统相册（MediaStore）中收纳的子相册名，落点形如 <c>Pictures/AnimeDownloader</c>。</summary>
    public string AlbumName { get; init; } = "AnimeDownloader";

    /// <summary>落点：系统相册 or 应用私有目录。默认相册（可见性优先）。</summary>
    public DownloadLocation Location { get; init; } = DownloadLocation.Gallery;

    /// <summary>true 时只在非计费网络（Wi‑Fi / 以太网）上传输，移动数据下会停在「等待网络」。</summary>
    public bool WifiOnly { get; init; }

    /// <summary>
    /// 并发传输数上限。≤ 0 表示<b>交给设备自适应</b>（见 <see cref="DownloadPolicy.ResolveConcurrency"/>）——
    /// 手机上的推荐值，避免在低内存机型上开太多连接把系统压垮。
    /// </summary>
    public int MaxConcurrentDownloads { get; init; }

    /// <summary>
    /// 整批跑完后，对失败项再补跑的<b>轮数</b>。默认 1 轮。
    /// </summary>
    /// <remarks>
    /// 传输层的瞬时重试由 Core 的 <c>ImageDownloader</c> 自己负责（超时 / 5xx / 429 / IO 会退避重试）。
    /// 这里是更高一层的<b>收尾补跑</b>：手机上断网往往持续几秒到几十秒，让某一项原地死等
    /// 会把并发闸门的一格白白占住；先跳过、等整批走完再统一补跑，网络通常已经恢复。
    /// </remarks>
    public int RetryCount { get; init; } = 1;

    /// <summary>false（默认）时，目标已存在同名非空文件即跳过，不重复下载。</summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>可用空间低于此值即拒绝开始，避免下到一半磁盘写满。</summary>
    public long MinimumFreeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>在「等待网络」状态下的最长等待时间；超时则整批以失败结束。</summary>
    public TimeSpan NetworkWaitTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>批次结束后是否保留通知栏结果通知。</summary>
    public bool NotifyOnCompletion { get; init; } = true;
}
