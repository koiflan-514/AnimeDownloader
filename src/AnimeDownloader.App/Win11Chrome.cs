using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
        // （透明会渲染成黑色），因此直接使用应用主题的背景色。
        var background = isDark
            ? Color.FromArgb(255, 0x12, 0x12, 0x16)
            : Color.FromArgb(255, 0xF6, 0xF6, 0xF9);

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
}
