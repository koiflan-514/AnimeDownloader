using Android.App;
using Android.Runtime;

namespace AnimeDownloader.App;

/// <summary>
/// 应用入口。
/// </summary>
/// <remarks>
/// <para>唯一的职责是<b>把组合根挂到进程上</b>：所有共享对象（HttpClient、下载器、图源、
/// 下载模块）都在这里创建一次，生命周期与进程一致。</para>
///
/// <para>挂在这里而不是 Activity 上，是因为批量下载会在用户离开界面、甚至锁屏之后继续 ——
/// 那时 Activity 可能已被销毁重建，而这些对象必须还活着。反过来，
/// <b>界面不该持有这些对象的引用超过它自己的生命周期</b>，否则就是把 Activity 泄漏进 Application。</para>
/// </remarks>
[Application(
    Label = "@string/app_name",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@style/AppTheme",
    // 不参与系统备份：配置文件里可能带 Gelbooru 的 API 凭据，
    // 而「备份到云端」这件事用户并没有在应用内同意过。
    AllowBackup = false,
    // 全部图源都是 HTTPS；明文流量一律关掉。
    UsesCleartextTraffic = false,
    // 界面全是自绘 + 位图，硬件加速是必须的（列表滚动与手势缩放都吃它）。
    HardwareAccelerated = true,
    // 不需要独立的大堆：缩略图走采样解码 + LRU 限额，靠加大堆来掩盖内存问题只会更晚崩。
    LargeHeap = false)]
internal sealed class AnimeDownloaderApplication : Application
{
    /// <summary>
    /// 进程内唯一的服务集合。在 <see cref="OnCreate"/> 里装配完成，
    /// 因此之后任何 Activity / View 读它都是非空的。
    /// </summary>
    internal static AppServices Services { get; private set; } = null!;

    /// <summary>运行时在「Java 侧创建了 Application、需要托管对等体」时调用的构造函数。</summary>
    /// <remarks>
    /// 没有它，进程启动时运行时会在类型加载阶段直接抛异常（DNAA0001）。
    /// 它对应用代码从不显式调用，只由绑定运行时使用。
    /// </remarks>
    public AnimeDownloaderApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    /// <inheritdoc />
    public override void OnCreate()
    {
        base.OnCreate();
        Services = AppServices.Create(this);
    }

    /// <inheritdoc />
    public override void OnTerminate()
    {
        // 只在模拟器上会被调用（真实设备上进程直接被杀）。
        // 仍然写全：这是释放 HttpClient / 下载器 / 前台服务引用的唯一正式时机。
        Services?.Dispose();
        base.OnTerminate();
    }
}
