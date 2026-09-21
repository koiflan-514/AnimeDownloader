namespace AnimeDownloader.Download.Contracts;

/// <summary>当前生效的网络类型。只关心与「是否开始传输」有关的四档。</summary>
public enum NetworkKind
{
    /// <summary>没有可用网络。</summary>
    None,

    /// <summary>Wi‑Fi（含 Wi‑Fi 直连、以太网）。</summary>
    Unmetered,

    /// <summary>移动数据（计费网络）。</summary>
    Cellular,

    /// <summary>其它网络，按<b>计费</b>处理（保守判定：不确定就不要偷偷花流量）。</summary>
    OtherMetered,
}

/// <summary>
/// 设备的资源画像，决定并发上限。放在契约层（而不是 Android 层）是为了让
/// 「低配机型该开几条连接」这条规则可被单元测试直接覆盖。
/// </summary>
/// <param name="ProcessorCount">逻辑核心数。</param>
/// <param name="MemoryClassMb">Android 给本应用的内存上限（<c>ActivityManager.MemoryClass</c>），单位 MB。</param>
/// <param name="IsLowRamDevice"><c>ActivityManager.IsLowRamDevice</c>：系统自报的低内存设备。</param>
public readonly record struct DeviceProfile(
    int ProcessorCount,
    int MemoryClassMb,
    bool IsLowRamDevice)
{
    /// <summary>核心数未知时的保守画像（2 核 / 128MB / 非低内存）。</summary>
    public static DeviceProfile Conservative { get; } = new(2, 128, false);
}

/// <summary>
/// 下载策略的纯决策部分：并发度、网络准入、空间预检。没有 Android 类型，可单测。
/// </summary>
/// <remarks>
/// 这几条规则在桌面端不存在，是<b>专门为手机加的</b>：桌面可以把并发开到 CPU 核数量级，
/// 手机的瓶颈不是 CPU 而是内存与电池 —— 一次解码十几张大图就足以触发低内存回收，
/// 把整个应用切到后台杀掉。因此这里的上限刻意压得很低（2–4）。
/// </remarks>
public static class DownloadPolicy
{
    /// <summary>手机上的并发区间。</summary>
    public const int MinConcurrency = 2;

    /// <summary>手机上的并发上限（即便是 12 核旗舰也不超过这个数）。</summary>
    public const int MaxConcurrency = 4;

    /// <summary>
    /// 解析最终并发数。
    /// <list type="bullet">
    ///   <item><description><paramref name="requested"/> &gt; 0：尊重用户设置，但仍夹到 1–8（允许用户为省电调到 1）。</description></item>
    ///   <item><description><paramref name="requested"/> ≤ 0（自动，含未设置时的默认 0）：按设备画像取 2–4，低内存设备取 2。</description></item>
    /// </list>
    /// </summary>
    public static int ResolveConcurrency(int requested, DeviceProfile profile)
    {
        if (requested > 0)
        {
            return Math.Clamp(requested, 1, 8);
        }

        var cores = Math.Max(1, profile.ProcessorCount);
        var byCores = cores switch
        {
            >= 8 => 4,
            >= 6 => 3,
            >= 4 => 3,
            _ => MinConcurrency,
        };

        // 低内存设备再压一档：这两条不是「保险」，而是实测过的两个独立信号 ——
        // IsLowRamDevice 由系统按总内存判定，MemoryClass 由系统按进程可用堆判定，
        // 一台 4GB 机器可能只触发其中一个。
        if (profile.IsLowRamDevice || profile.MemoryClassMb is > 0 and < 192)
        {
            byCores = Math.Min(byCores, MinConcurrency);
        }

        return Math.Clamp(byCores, MinConcurrency, MaxConcurrency);
    }

    /// <summary>
    /// 网络准入：当前网络是否允许开始/继续传输。
    /// 开启「仅 Wi‑Fi」后，<see cref="NetworkKind.OtherMetered"/> 同样被拦下 —— 无法识别的
    /// 网络类型一律按计费处理，宁可等一等，也不要在用户不知情时走流量。
    /// </summary>
    public static bool AllowsTransfer(NetworkKind kind, bool wifiOnly) => kind switch
    {
        NetworkKind.None => false,
        NetworkKind.Unmetered => true,
        _ => !wifiOnly,
    };

    /// <summary>上传/下载通用的一句话说明，直接可用于状态栏与通知。</summary>
    public static string DescribeNetwork(NetworkKind kind, bool wifiOnly) => kind switch
    {
        NetworkKind.None => "等待网络连接…",
        NetworkKind.Unmetered => "已连接 Wi‑Fi",
        _ when wifiOnly => "已开启「仅 Wi‑Fi」，正在等待可用 Wi‑Fi…",
        NetworkKind.Cellular => "正在使用移动数据",
        _ => "正在使用计费网络",
    };

    /// <summary>
    /// 空间预检。返回 null 表示可以通过；否则返回给用户看的一句话原因。
    /// </summary>
    /// <param name="freeBytes">落点所在卷的可用字节数。</param>
    /// <param name="expectedBytes">本批预计需要的字节数；未知总量时传 null（只做下限检查）。</param>
    /// <param name="minimumFreeBytes">策略要求的可用空间下限。</param>
    public static string? CheckStorage(long freeBytes, long? expectedBytes, long minimumFreeBytes)
    {
        if (freeBytes < minimumFreeBytes)
        {
            return $"可用空间不足：剩余 {FormatBytes(freeBytes)}，至少需要 {FormatBytes(minimumFreeBytes)}";
        }

        // 已知总量时再留 10% 余量：MediaStore 写入过程中系统还要生成缩略图、写数据库。
        if (expectedBytes is > 0)
        {
            var required = (long)(expectedBytes.Value * 1.1);
            if (freeBytes < required)
            {
                return $"可用空间不足：本批约需 {FormatBytes(required)}，当前剩余 {FormatBytes(freeBytes)}";
            }
        }

        return null;
    }

    /// <summary>
    /// 把失败归因成人话。HTTP 状态与 IO 错误在手机上很常见（切网、锁屏），
    /// 原文对用户没有意义，这里收敛成可行动的短语。
    /// </summary>
    public static string DescribeFailure(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? "已取消" : "网络超时";
        }

        if (exception is HttpRequestException http)
        {
            return http.StatusCode switch
            {
                null => "网络不可达",
                System.Net.HttpStatusCode.NotFound => "图片不存在（404）",
                System.Net.HttpStatusCode.Forbidden => "被图源拒绝（403）",
                System.Net.HttpStatusCode.TooManyRequests => "请求过于频繁（429）",
                System.Net.HttpStatusCode.Unauthorized => "需要图源凭据（401）",
                { } s when (int)s >= 500 => "图源服务异常（5xx）",
                { } s => $"HTTP {(int)s}",
            };
        }

        if (exception is IOException)
        {
            return "写入失败（存储空间或权限）";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "没有写入权限";
        }

        return exception.Message;
    }

    /// <summary>字节数的人类可读形式（1 位小数）。等宽字体下这一串数字是界面的一部分。</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
