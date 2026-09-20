using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace AnimeDownloader.App.Platform;

/// <summary>
/// 窗口外壳（WinUI 版的 <c>Win11Chrome</c>）：标题栏配色、首屏与最小尺寸、整窗全屏。
/// 主窗口与独立大图窗口共用。
///
/// 与 WinUI 版的两处结构性差异：
///   1. **最小尺寸不再需要 DPI 换算。** WinUI 的 <c>OverlappedPresenter.PreferredMinimumWidth</c>
///      收的是物理像素，逻辑值必须乘 RasterizationScale；Avalonia 的 <c>Window.MinWidth</c>
///      本来就是 DIP，框架替我们做了换算。于是「把 980 逻辑当 980 物理设下去、
///      在 125% 缩放下白设」这个坑在 Avalonia 侧不存在。
///   2. **全屏不再需要手工还原几何。** WinUI 要把 <c>SetPresenter(FullScreen)</c> 前的位置尺寸
///      自己存下来，并且退出时**必须重设一次最小尺寸** —— 因为 <c>SetPresenter(Overlapped)</c>
///      拿到的是另一个 OverlappedPresenter 实例，原来的约束挂在旧实例上。
///      Avalonia 的 <c>WindowState.FullScreen</c> 是窗口自身的状态位，窗口对象没被换掉，
///      MinWidth/MinHeight 一直有效；位置尺寸由框架还原。这里只需额外记住
///      「进全屏前是不是最大化的」，好让退出时回到最大化而不是普通态。
/// </summary>
internal static class WindowChrome
{
    /// <summary>窗口最小逻辑宽度：低于它命令条（图源 + 过滤 + 状态 + 操作）会开始互相压盖。</summary>
    public const double MinLogicalWidth = 980;

    /// <summary>窗口最小逻辑高度。</summary>
    public const double MinLogicalHeight = 620;

    /// <summary>首屏逻辑尺寸（会按工作区再夹一次）。</summary>
    private const double PreferredLogicalWidth = 1360;

    /// <summary>首屏逻辑高度。</summary>
    private const double PreferredLogicalHeight = 860;

    /// <summary>窗口与工作区边缘保留的逻辑间距。</summary>
    private const double LogicalMargin = 48;

    // ---------------- DWM 标题栏 ----------------

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>每个窗口的外壳状态：是否已挂过主题监听、进全屏前是否最大化。</summary>
    private sealed class ChromeState
    {
        public bool Hooked;
        public bool HasPreFullscreenState;
        public bool WasMaximized;
    }

    private static readonly ConditionalWeakTable<Window, ChromeState> States = new();

    private static ChromeState StateFor(Window window) => States.GetOrCreateValue(window);

    /// <summary>
    /// 设置应用图标、首屏尺寸、最小尺寸与标题栏配色，并挂上主题变化后的标题栏重刷。
    /// 对应 WinUI 版 <c>Win11Chrome.Apply</c> + <c>SetInitialBounds</c> + <c>ApplyAfterThemeChange</c> 三者的合并。
    /// </summary>
    public static void Apply(Window window, bool withInitialBounds)
    {
        SetIcon(window);
        ApplyTitleBar(window);

        var state = StateFor(window);
        if (!state.Hooked)
        {
            state.Hooked = true;
            // 标题栏颜色不会跟着应用内主题切换自动变（系统只跟系统主题），
            // 因此每次主题变化后要显式重刷一次 —— 与 WinUI 版同一处理。
            window.ActualThemeVariantChanged += (_, _) => ApplyTitleBar(window);
        }

        if (withInitialBounds)
        {
            ApplyMinimumSize(window);
            SetInitialBounds(window);
        }
    }

    /// <summary>
    /// 最小尺寸不是审美偏好，是硬约束：命令条上「图源 / 过滤 / 状态 / 操作」四组控件
    /// 低于约 980 逻辑像素就开始互相压盖 —— 分段器会被右端状态文字切掉一半。
    /// </summary>
    public static void ApplyMinimumSize(Window window)
    {
        window.MinWidth = MinLogicalWidth;
        window.MinHeight = MinLogicalHeight;
    }

    /// <summary>首屏尺寸：取一个「一屏看得全」的起点，并按显示器工作区收边，免得在 1280×720 的机器上开局就超屏。</summary>
    public static void SetInitialBounds(Window window)
    {
        try
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null)
            {
                return;
            }

            // WorkingArea 是物理像素，除以 Scaling 换回逻辑像素。
            var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var workWidth = screen.WorkingArea.Width / scale;
            var workHeight = screen.WorkingArea.Height / scale;
            var width = Math.Min(PreferredLogicalWidth, workWidth - (LogicalMargin * 2));
            var height = Math.Min(PreferredLogicalHeight, workHeight - (LogicalMargin * 2));
            if (width <= 0 || height <= 0)
            {
                return;
            }

            window.Width = width;
            window.Height = height;
        }
        catch (Exception ex)
        {
            // 尺寸设置失败不影响启动：窗口就停在系统默认大小。
            TryLog($"WindowChrome.SetInitialBounds failed: {ex}");
        }
    }

    /// <summary>设置窗口图标（与 exe 一起复制到输出目录）。</summary>
    public static void SetIcon(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AnimeDownloader.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            window.Icon = new WindowIcon(iconPath);
        }
        catch (Exception)
        {
            // 图标失败不影响启动。
        }
    }

    /// <summary>
    /// 标题栏配色。
    ///
    /// 为什么走 DWM 而不是 Avalonia 的属性：WinUI 版是设
    /// <c>AppWindow.TitleBar.BackgroundColor/ForegroundColor/ButtonHover*</c> 一整组，
    /// 底层就是 DWM 的 caption/text/border 三个属性；Avalonia 只在
    /// <c>TopLevel.SetSystemBarColor</c> 上暴露了「系统条颜色」，够不到文字色，
    /// 而且换亮暗主题时不会自动重刷。直接写 DWM 属性与 WinUI 版等价，也最可控。
    ///
    /// 注意背景用「房间底色」而不是透明：系统绘制的标题栏（未启用
    /// ExtendsContentIntoTitleBar / ExtendClientAreaToDecorationsHint）不支持透明 ——
    /// 透明会渲染成黑色。所以让标题栏与画布同一个颜色，连成一片。
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var isDark = window.ActualThemeVariant == ThemeVariant.Dark;
            var background = isDark
                ? Color.FromArgb(255, 0x0D, 0x0E, 0x11)
                : Color.FromArgb(255, 0xF7, 0xF7, 0xF9);
            var foreground = isDark
                ? Color.FromArgb(255, 235, 235, 235)
                : Color.FromArgb(255, 20, 20, 20);

            // 深色沉浸模式：让系统绘制的按钮（最小化/最大化/关闭）也跟着换成浅色。
            SetDwmInt(handle, DwmwaUseImmersiveDarkMode, isDark ? 1 : 0);
            SetDwmColor(handle, DwmwaCaptionColor, background);
            SetDwmColor(handle, DwmwaTextColor, foreground);
        }
        catch (Exception ex)
        {
            TryLog($"WindowChrome.ApplyTitleBar failed: {ex}");
        }
    }

    /// <summary>DWM 的窗口颜色参数收 COLORREF（0x00BBGGRR），不是 ARGB —— 顺序不能照抄。</summary>
    private static void SetDwmColor(IntPtr hwnd, int attribute, Color color)
    {
        var colorref = color.R | (color.G << 8) | (color.B << 16);
        _ = DwmSetWindowAttribute(hwnd, attribute, ref colorref, sizeof(int));
    }

    private static void SetDwmInt(IntPtr hwnd, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ==================== 全屏（F11） ====================

    /// <summary>窗口当前是否全屏。</summary>
    public static bool IsFullscreen(Window window) => window.WindowState == WindowState.FullScreen;

    /// <summary>整窗全屏开关。</summary>
    public static void SetFullscreen(Window window, bool on)
    {
        if (on == IsFullscreen(window))
        {
            return;
        }

        try
        {
            var state = StateFor(window);
            if (on)
            {
                // 被最大化过的窗口退出全屏时不会自己回到最大化，必须先记下来。
                state.WasMaximized = window.WindowState == WindowState.Maximized;
                state.HasPreFullscreenState = true;
                window.WindowState = WindowState.FullScreen;
                return;
            }

            window.WindowState = state.HasPreFullscreenState && state.WasMaximized
                ? WindowState.Maximized
                : WindowState.Normal;

            // 退出全屏时窗口对象没被替换，MinWidth/MinHeight 一直在 —— 但这里再设一次
            // 是廉价的保险（WinUI 版就是因为呈现器被换掉而必须重设，本版理论上不需要）。
            ApplyMinimumSize(window);
            state.HasPreFullscreenState = false;
        }
        catch (Exception ex)
        {
            TryLog($"WindowChrome.SetFullscreen(on: {on}) failed: {ex}");
        }
    }

    private static void TryLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {message}\n");
        }
        catch
        {
            // 日志写入失败不阻止运行。
        }
    }
}
