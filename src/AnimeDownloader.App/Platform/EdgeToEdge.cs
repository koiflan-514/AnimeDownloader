using Android.App;
using Android.Graphics;
using Android.Views;

namespace AnimeDownloader.App.Platform;

/// <summary>
/// 边到边（edge-to-edge）与系统栏内边距。
/// </summary>
/// <remarks>
/// <para><b>为什么必须处理</b>：Android 15（API 35）起边到边是<b>强制</b>的 ——
/// 应用的内容会铺满整屏，状态栏与手势条直接压在内容之上。不做内边距补偿的界面，
/// 症状是「标题被状态栏盖住」「底部按钮被手势条挡住」，而且在低版本上还看不出来。</para>
///
/// <para>这里用平台 API 而不是 AndroidX 的 <c>WindowInsetsCompat</c>：本应用的主题不依赖
/// AppCompat/Material，为一行内边距再拖进 AndroidX.Core 不划算。分档只有两条：</para>
/// <list type="bullet">
///   <item><description>API 30+：<c>SetDecorFitsSystemWindows(false)</c> + <c>GetInsets(Type)</c>。</description></item>
///   <item><description>API 24–29：主题里已经把系统栏设成透明，这里读老的
///   <c>SystemWindowInset*</c> 即可。</description></item>
/// </list>
/// </remarks>
internal static class EdgeToEdge
{
    /// <summary>四边内边距（px）。</summary>
    /// <param name="Left">左边（横屏时的挖孔 / 刘海侧）。</param>
    /// <param name="Top">状态栏高度。</param>
    /// <param name="Right">右边。</param>
    /// <param name="Bottom">手势条或键盘高度（取较大者）。</param>
    internal readonly record struct Insets(int Left, int Top, int Right, int Bottom)
    {
        internal static Insets Empty { get; } = new(0, 0, 0, 0);
    }

    /// <summary>
    /// 让窗口内容铺满全屏，并在内边距变化时回调。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="root">根视图（内边距由调用方在回调里施加）。</param>
    /// <param name="onInsets">内边距变化回调。第一次会在布局前就触发一次。</param>
    internal static void Apply(Activity activity, View root, Action<Insets> onInsets)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(onInsets);

        // API 30-34：显式声明「内容自己处理边到边」。
        // API 35 起边到边由系统强制、该方法已废弃 —— 那一档什么都不用做。
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            activity.Window?.SetDecorFitsSystemWindows(false);
        }

        root.SetOnApplyWindowInsetsListener(new InsetsListener(onInsets));
        // 请求一次初始分发：某些设备上首个 insets 会在 addView 之前就到，
        // 不主动请求的话监听器可能一直不触发，界面就会顶到状态栏下面。
        root.RequestApplyInsets();
    }

    /// <summary>从 <see cref="WindowInsets"/> 里读出四边内边距（含键盘）。</summary>
    internal static Insets Read(WindowInsets? insets)
    {
        if (insets is null)
        {
            return Insets.Empty;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            var ime = insets.GetInsets(WindowInsets.Type.Ime());
            // 键盘与手势条不同时占位，取较大的那个 —— 相加会让内容被顶飞。
            return new Insets(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
        }

        return new Insets(
            insets.SystemWindowInsetLeft,
            insets.SystemWindowInsetTop,
            insets.SystemWindowInsetRight,
            insets.SystemWindowInsetBottom);
    }

    /// <summary>把系统栏图标切成与背景相配的深/浅色。</summary>
    /// <remarks>
    /// 亮色主题下状态栏是浅底，图标必须转深色，否则时间是白的、看不见。
    /// API 30+ 走 <c>InsetsController</c>，更低版本走 <c>SystemUiFlags</c> 位。
/// 刻意不用 <c>SystemUiVisibility</c>：那个属性在绑定里标了过时，因为它把标志位声明成了
/// <c>StatusBarVisibility</c>（成员名与真实标志并不对应，例如根本没有 LightStatusBar）。
/// <c>SystemUiFlags</c> 是同一个底层调用的正确类型化入口。
    /// </remarks>
    internal static void ApplySystemBarContrast(Activity activity, bool lightBackground)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var window = activity.Window;
        if (window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = window.InsetsController;
            if (controller is null)
            {
                return;
            }

            const int lights = (int)(WindowInsetsControllerAppearance.LightStatusBars
                | WindowInsetsControllerAppearance.LightNavigationBars);
            // 第二个参数是「本次要覆盖的位掩码」，所以关闭亮色时必须把它一起传进去，
            // 只传 0 是什么都不改 —— 状态栏图标会停在上一次的配色上。
            controller.SetSystemBarsAppearance(
                lightBackground ? lights : 0,
                lights);
            return;
        }

        var decor = window.DecorView;
        if (decor is null)
        {
            return;
        }

        var flags = decor.SystemUiFlags;
        if (lightBackground)
        {
            flags |= SystemUiFlags.LightStatusBar;
        }
        else
        {
            flags &= ~SystemUiFlags.LightStatusBar;
        }

        decor.SystemUiFlags = flags;
    }

    /// <summary>窗外全屏时也把系统栏一起收起来（灯箱用）。</summary>
    internal static void SetBarsHidden(Activity activity, bool hidden)
    {
        var window = activity.Window;
        if (window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = window.InsetsController;
            if (controller is null)
            {
                return;
            }

            if (hidden)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                // 手势唤出的临时系统栏不改变布局（否则看大图时画面会突然抖一下）。
                // 这个属性在绑定里是裸 int（Java 侧就是 int 常量），所以显式转一次。
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else
            {
                controller.Show(WindowInsets.Type.SystemBars());
            }

            return;
        }

        var decor = window.DecorView;
        if (decor is null)
        {
            return;
        }

        const SystemUiFlags fullscreenFlags =
            SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.Immersive;
        var flags = decor.SystemUiFlags;
        decor.SystemUiFlags = hidden ? flags | fullscreenFlags : flags & ~fullscreenFlags;
    }

    private sealed class InsetsListener(Action<Insets> onInsets) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            onInsets(Read(insets));
            // 原样返回：不消费 insets，否则同级的其它视图收不到分发。
            return insets;
        }
    }
}
