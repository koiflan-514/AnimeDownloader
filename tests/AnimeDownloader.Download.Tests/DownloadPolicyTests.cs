using System.Net;
using AnimeDownloader.Download.Contracts;

namespace AnimeDownloader.Download.Tests;

/// <summary>
/// 并发度、网络准入、空间预检与失败归因的测试。
/// </summary>
/// <remarks>
/// 这一组规则是桌面端<b>没有</b>的，也是这个模块存在的主要理由：
/// 桌面把并发开到 CPU 核数量级没有代价，手机上线开多了会触发低内存回收把应用杀掉。
/// 规则一旦写错，症状是「旗舰机上一切正常、低端机上批量下载必崩」—— 属于最难复现的那类问题，
/// 所以这几条必须有断言兜住。
/// </remarks>
public sealed class DownloadPolicyTests
{
    [Fact]
    public void ResolveConcurrency_用户显式指定时尊重设置()
    {
        Assert.Equal(1, DownloadPolicy.ResolveConcurrency(1, new DeviceProfile(8, 512, false)));
        Assert.Equal(6, DownloadPolicy.ResolveConcurrency(6, DeviceProfile.Conservative));
    }

    [Fact]
    public void ResolveConcurrency_用户显式指定时仍夹在1到8之间() =>
        Assert.Equal(8, DownloadPolicy.ResolveConcurrency(64, new DeviceProfile(16, 512, false)));

    [Fact]
    public void ResolveConcurrency_非正值一律按自动处理()
    {
        // 契约是「> 0 才算显式指定」，于是 0 与负数走同一条自适应路径。
        foreach (var requested in new[] { 0, -1, -3 })
        {
            Assert.Equal(
                DownloadPolicy.ResolveConcurrency(0, DeviceProfile.Conservative),
                DownloadPolicy.ResolveConcurrency(requested, DeviceProfile.Conservative));
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    public void ResolveConcurrency_自动模式按核心数取值(int cores, int expected) =>
        Assert.Equal(expected, DownloadPolicy.ResolveConcurrency(0, new DeviceProfile(cores, 512, false)));

    [Fact]
    public void ResolveConcurrency_自动模式永不超出手机上限()
    {
        var resolved = DownloadPolicy.ResolveConcurrency(0, new DeviceProfile(64, 2048, false));
        Assert.Equal(DownloadPolicy.MaxConcurrency, resolved);
    }

    [Fact]
    public void ResolveConcurrency_低内存设备压到下限()
    {
        // 八核的低内存机（约 1–2GB）比四核的中端机更危险：内存不够时应用会被系统直接杀掉。
        Assert.Equal(
            DownloadPolicy.MinConcurrency,
            DownloadPolicy.ResolveConcurrency(0, new DeviceProfile(8, 512, IsLowRamDevice: true)));
    }

    [Fact]
    public void ResolveConcurrency_进程堆上限过低时同样压低()
    {
        Assert.Equal(
            DownloadPolicy.MinConcurrency,
            DownloadPolicy.ResolveConcurrency(0, new DeviceProfile(8, 96, IsLowRamDevice: false)));
    }

    [Fact]
    public void ResolveConcurrency_核心数报0时不崩且取下限()
    {
        var resolved = DownloadPolicy.ResolveConcurrency(0, new DeviceProfile(0, 0, false));
        Assert.InRange(resolved, DownloadPolicy.MinConcurrency, DownloadPolicy.MaxConcurrency);
    }

    [Theory]
    [InlineData(NetworkKind.None, false, false)]
    [InlineData(NetworkKind.None, true, false)]
    [InlineData(NetworkKind.Unmetered, false, true)]
    [InlineData(NetworkKind.Unmetered, true, true)]
    [InlineData(NetworkKind.Cellular, false, true)]
    [InlineData(NetworkKind.Cellular, true, false)]
    [InlineData(NetworkKind.OtherMetered, false, true)]
    [InlineData(NetworkKind.OtherMetered, true, false)]
    public void AllowsTransfer_仅WiFi时拦下所有计费网络(NetworkKind kind, bool wifiOnly, bool expected) =>
        Assert.Equal(expected, DownloadPolicy.AllowsTransfer(kind, wifiOnly));

    [Fact]
    public void CheckStorage_空间充足时放行() =>
        Assert.Null(DownloadPolicy.CheckStorage(1024L * 1024 * 1024, 10L * 1024 * 1024, 64L * 1024 * 1024));

    [Fact]
    public void CheckStorage_低于下限时给出可读原因()
    {
        var reason = DownloadPolicy.CheckStorage(10L * 1024 * 1024, null, 64L * 1024 * 1024);

        Assert.NotNull(reason);
        Assert.Contains("空间不足", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckStorage_已知总量时额外留出一成余量()
    {
        // 空闲 100MB、本批约 95MB：只看「够不够放」是够的，但 MediaStore 写入期间
        // 系统还要生成缩略图、写数据库，留 10% 余量才不会下到一半失败。
        const long free = 100L * 1024 * 1024;
        const long batch = 95L * 1024 * 1024;

        Assert.NotNull(DownloadPolicy.CheckStorage(free, batch, 64L * 1024 * 1024));
        Assert.Null(DownloadPolicy.CheckStorage(free, 10L * 1024 * 1024, 64L * 1024 * 1024));
    }

    [Fact]
    public void DescribeNetwork_仅WiFi且正在走流量时说明原因()
    {
        var text = DownloadPolicy.DescribeNetwork(NetworkKind.Cellular, wifiOnly: true);

        Assert.Contains("等待可用", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_取消与超时区分开()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal("已取消", DownloadPolicy.DescribeFailure(new OperationCanceledException(), cancelled.Token));
        Assert.Equal("网络超时", DownloadPolicy.DescribeFailure(new OperationCanceledException(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "图片不存在（404）")]
    [InlineData(HttpStatusCode.Forbidden, "被图源拒绝（403）")]
    [InlineData(HttpStatusCode.TooManyRequests, "请求过于频繁（429）")]
    [InlineData(HttpStatusCode.Unauthorized, "需要图源凭据（401）")]
    [InlineData(HttpStatusCode.InternalServerError, "图源服务异常（5xx）")]
    [InlineData(HttpStatusCode.BadGateway, "图源服务异常（5xx）")]
    public void DescribeFailure_HTTP状态收敛成可行动短语(HttpStatusCode status, string expected) =>
        Assert.Equal(
            expected,
            DownloadPolicy.DescribeFailure(new HttpRequestException("x", null, status), CancellationToken.None));

    [Fact]
    public void DescribeFailure_无状态码的网络错误报不可达() =>
        Assert.Equal(
            "网络不可达",
            DownloadPolicy.DescribeFailure(new HttpRequestException("boom"), CancellationToken.None));

    [Fact]
    public void DescribeFailure_落盘错误指向存储() =>
        Assert.Equal(
            "写入失败（存储空间或权限）",
            DownloadPolicy.DescribeFailure(new IOException("x"), CancellationToken.None));

    [Fact]
    public void DescribeFailure_无权限指向权限() =>
        Assert.Equal(
            "没有写入权限",
            DownloadPolicy.DescribeFailure(new UnauthorizedAccessException(), CancellationToken.None));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(3L * 1024 * 1024, "3 MB")]
    // 2.5 GiB —— 注意不能写成 (long)2.5 * …：那会被截断成 2。
    [InlineData(2_684_354_560L, "2.5 GB")]
    public void FormatBytes_按量级取单位(long bytes, string expected) =>
        Assert.Equal(expected, DownloadPolicy.FormatBytes(bytes));
}

/// <summary>
/// 快照与不可变契约的算术。
/// </summary>
public sealed class DownloadSnapshotTests
{
    private static DownloadItemSnapshot Item(DownloadItemState state) =>
        new(0, "a.png", "https://x/a.png", state, 100, 200);

    [Fact]
    public void Fraction_总长度未知时为null而不是0()
    {
        var snapshot = new DownloadItemSnapshot(0, "a.png", "https://x/a.png", DownloadItemState.Running, 100, null);

        // 未知总长度必须与「0%」区分开：界面据此显示不确定进度条，
        // 而不是画一条永远不动的 0% 进度条骗人。
        Assert.Null(snapshot.Fraction);
    }

    [Fact]
    public void Fraction_夹在0到1之间()
    {
        Assert.Equal(0.5, Item(DownloadItemState.Running).Fraction);
        Assert.Equal(
            1,
            new DownloadItemSnapshot(0, "a", "u", DownloadItemState.Running, 500, 200).Fraction);
    }

    [Theory]
    [InlineData(DownloadItemState.Queued, false)]
    [InlineData(DownloadItemState.Running, false)]
    [InlineData(DownloadItemState.Completed, true)]
    [InlineData(DownloadItemState.Skipped, true)]
    [InlineData(DownloadItemState.Failed, true)]
    [InlineData(DownloadItemState.Cancelled, true)]
    public void IsFinished_只有终态为真(DownloadItemState state, bool expected) =>
        Assert.Equal(expected, Item(state).IsFinished);

    [Fact]
    public void BatchFraction_按项数而不是字节数()
    {
        // 各图大小悬殊，若按字节数算比例，一张 20MB 的图就能让进度条长时间停在个位数。
        var snapshot = new DownloadBatchSnapshot(
            DownloadBatchState.Running,
            4,
            2,
            0,
            0,
            100,
            10_000_000,
            null,
            [
                Item(DownloadItemState.Completed),
                Item(DownloadItemState.Completed),
                Item(DownloadItemState.Queued),
                Item(DownloadItemState.Queued),
            ]);

        Assert.Equal(0.5, snapshot.Fraction);
        Assert.Equal(2, snapshot.Finished);
        Assert.True(snapshot.HasPending);
    }

    [Fact]
    public void BatchFraction_空批次为0且不抛除零()
    {
        Assert.Equal(0, DownloadBatchSnapshot.Idle.Fraction);
        Assert.False(DownloadBatchSnapshot.Idle.HasPending);
    }

    [Fact]
    public void IsClean_失败或取消都不算干净()
    {
        var arrays = (IReadOnlyList<string>)Array.Empty<string>();
        var indices = (IReadOnlyList<int>)Array.Empty<int>();

        Assert.True(new DownloadBatchResult(3, 0, 0, false, "loc", arrays, arrays, indices).IsClean);
        Assert.False(new DownloadBatchResult(3, 0, 1, false, "loc", arrays, arrays, indices).IsClean);
        Assert.False(new DownloadBatchResult(3, 0, 0, true, "loc", arrays, arrays, indices).IsClean);
    }
}
