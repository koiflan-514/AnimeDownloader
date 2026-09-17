using System.Runtime.CompilerServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AnimeDownloader.App;

/// <summary>
/// Shared Win11 window chrome helper: Mica backdrop, theme-aware title bar colors and
/// the application icon. Applies to both the main window and the gallery viewer window.
/// 标题栏背景/前景按应用当前主题（ActualTheme）设置——系统默认标题栏只跟随"系统"主题，
/// 不跟应用内切换，因此这里在每次主题变化后显式重刷。
/// </summary>
internal static class Win11Chrome
{
    /// <summary>窗口最小逻辑宽度：低于它命令条（图源 + 过滤 + 状态 + 操作）会开始互相压盖。</summary>
    private const double MinLogicalWidth = 980;

    /// <summary>窗口最小逻辑高度。</summary>
    private const double MinLogicalHeight = 620;

    /// <summary>首屏逻辑尺寸（会按工作区再夹一次）。</summary>
    private const double PreferredLogicalWidth = 1360;

    /// <summary>首屏逻辑高度。</summary>
    private const double PreferredLogicalHeight = 860;

    /// <summary>窗口与工作区边缘保留的逻辑间距。</summary>
    private const double LogicalMargin = 48;

    public static void Apply(Window window, FrameworkElement themeRoot)
    {
        // Mica 背景一个窗口只挂一次：主题切换时 Apply 会被 ActualThemeChanged 与
        // ApplySettings 连续触发，反复替换 SystemBackdrop 会在过渡期触发原生访问冲突。
        if (window.SystemBackdrop is null && Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
        }

        var isDark = themeRoot.ActualTheme == ElementTheme.Dark;
        var titleBar = window.AppWindow.TitleBar;
        var foreground = isDark
            ? Color.FromArgb(255, 235, 235, 235)
            : Color.FromArgb(255, 20, 20, 20);
        var inactiveForeground = isDark
            ? Color.FromArgb(140, 235, 235, 235)
            : Color.FromArgb(140, 20, 20, 20);
        var hover = isDark
            ? Color.FromArgb(230, 235, 235, 235)
            : Color.FromArgb(230, 30, 30, 30);
        // 悬停/按下时图标反色：深色主题浅底配深色图标，浅色主题深底配浅色图标
        var hoverForeground = isDark
            ? Color.FromArgb(255, 20, 20, 20)
            : Color.FromArgb(255, 240, 240, 240);
        // 系统绘制的标题栏（未启用 ExtendsContentIntoTitleBar）不支持透明背景
        // （透明会渲染成黑色），因此直接使用应用主题的「房间底色」，
        // 让标题栏与画布连成一片（暗房：#0D0E11 / 明室：#F7F7F9）。
        var background = isDark
            ? Color.FromArgb(255, 0x0D, 0x0E, 0x11)
            : Color.FromArgb(255, 0xF7, 0xF7, 0xF9);

        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = hoverForeground;
        titleBar.ButtonPressedBackgroundColor = hover;
        titleBar.ButtonPressedForegroundColor = hoverForeground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    /// <summary>
    /// 主题切换后刷新标题栏颜色。经 DispatcherQueue 延后一拍执行，确保在框架完成
    /// 主题过渡后读取到新的 ActualTheme（事件触发瞬间读取可能仍是旧值）。
    /// </summary>
    public static void ApplyAfterThemeChange(Window window, FrameworkElement themeRoot)
    {
        themeRoot.DispatcherQueue.TryEnqueue(() => Apply(window, themeRoot));
    }

    public static void SetIcon(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AnimeDownloader.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            window.AppWindow.SetIcon(iconPath);
        }
        catch (Exception)
        {
            // Icon failure must not break startup.
        }
    }

    /// <summary>
    /// 首屏尺寸与最小尺寸。须在窗口 Loaded 之后调用（需要 XamlRoot 的 DPI 换算）。
    ///
    /// 最小尺寸在这里不是审美偏好，是硬约束：命令条上「图源 / 过滤 / 状态 / 操作」
    /// 四组控件低于约 980 逻辑像素就开始互相压盖 —— 分段器会被右端状态文字切掉一半。
    /// 之前窗口可以一路拖小，于是「排版坏了」和「窗口太小」这两件事被混在一起。
    /// 首屏尺寸则取一个「一屏看得全」的起点，并按显示器工作区收边，
    /// 免得在 1280×720 的机器上开局就超屏。
    /// </summary>
    public static void SetInitialBounds(Window window, FrameworkElement themeRoot)
    {
        try
        {
            var scale = ScaleOf(themeRoot);
            var appWindow = window.AppWindow;
            // 尺寸单位一律是物理像素，逻辑值要乘 DPI 缩放再交给 AppWindow；
            // 显示器缩放 125% 时把「980 逻辑」当 980 物理设下去，等于只留了 784 逻辑，白设。
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                ApplyMinimumSize(presenter, scale);
                StateFor(appWindow).HasMinimumSize = true;
            }

            var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var margin = (int)Math.Round(LogicalMargin * scale);
            var width = Math.Min((int)Math.Round(PreferredLogicalWidth * scale), work.Width - (margin * 2));
            var height = Math.Min((int)Math.Round(PreferredLogicalHeight * scale), work.Height - (margin * 2));
            if (width <= 0 || height <= 0)
            {
                return;
            }

            appWindow.Resize(new SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            // 尺寸设置失败不影响启动：窗口就停在系统默认大小。
            TryLog($"Win11Chrome.SetInitialBounds failed: {ex}");
        }
    }

    private static double ScaleOf(FrameworkElement themeRoot)
    {
        var scale = themeRoot.XamlRoot?.RasterizationScale ?? 1.0;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// 最小尺寸必须按物理像素给（逻辑值 × DPI 缩放）。抽出来是因为它有第二个调用点：
    /// 退出全屏时 OverlappedPresenter 是新实例，要重设一次，否则约束就丢了。
    /// </summary>
    private static void ApplyMinimumSize(OverlappedPresenter presenter, double scale)
    {
        presenter.PreferredMinimumWidth = (int)Math.Round(MinLogicalWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(MinLogicalHeight * scale);
    }

    // ==================== 全屏（F11） ====================

    /// <summary>每个窗口的外壳状态：最小尺寸是否设过、进全屏前的几何与最大化状态。</summary>
    private sealed class WindowChromeState
    {
        public bool HasMinimumSize { get; set; }

        public bool HasBounds { get; set; }

        public RectInt32 Bounds { get; set; }

        public bool WasMaximized { get; set; }
    }

    private static readonly ConditionalWeakTable<AppWindow, WindowChromeState> ChromeStates = new();

    private static WindowChromeState StateFor(AppWindow appWindow) =>
        ChromeStates.GetOrCreateValue(appWindow);

    /// <summary>窗口当前是否处于全屏。</summary>
    public static bool IsFullscreen(Window window) =>
        window.AppWindow.Presenter is FullScreenPresenter;

    /// <summary>
    /// 全屏开关。集中处理三件容易漏掉的事：
    ///
    /// 1. **退出全屏后必须重设最小尺寸。** `SetPresenter(Overlapped)` 新建的是另一个
    ///    `OverlappedPresenter` 实例，`SetInitialBounds` 设过的 `PreferredMinimum*` 挂在
    ///    旧实例上。不重设的话，F11 进出一轮窗口就又能被拖到命令条互相压盖的大小 ——
    ///    等于把上一轮修好的问题放回来。
    /// 2. **还原进全屏前的几何与最大化状态。** 被最大化过的窗口退出全屏时不会自己回到
    ///    最大化，需要显式判断。
    /// 3. 只对 Overlapped / FullScreen 两种呈现器操作，其余状态（如紧凑浮层）直接不动。
    /// </summary>
    public static void SetFullscreen(Window window, FrameworkElement themeRoot, bool on)
    {
        var appWindow = window.AppWindow;
        if (on == IsFullscreen(window))
        {
            return;
        }

        try
        {
            var state = StateFor(appWindow);

            if (on)
            {
                if (appWindow.Presenter is OverlappedPresenter overlapped)
                {
                    state.Bounds = new RectInt32(
                        appWindow.Position.X,
                        appWindow.Position.Y,
                        appWindow.Size.Width,
                        appWindow.Size.Height);
                    state.WasMaximized = overlapped.State == OverlappedPresenterState.Maximized;
                    state.HasBounds = true;
                }

                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                return;
            }

            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is not OverlappedPresenter restored)
            {
                return;
            }

            // 只有本来就有最小尺寸约束的窗口才恢复它（独立大图窗口没有这个约束）。
            if (state.HasMinimumSize)
            {
                ApplyMinimumSize(restored, ScaleOf(themeRoot));
            }

            if (!state.HasBounds)
            {
                return;
            }

            if (state.WasMaximized)
            {
                restored.Maximize();
            }
            else
            {
                appWindow.MoveAndResize(state.Bounds);
            }
        }
        catch (Exception ex)
        {
            TryLog($"Win11Chrome.SetFullscreen(on: {on}) failed: {ex}");
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
