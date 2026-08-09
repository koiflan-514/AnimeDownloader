using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimeDownloader.App;

/// <summary>
/// Shared Win11 window chrome helper: Mica backdrop, theme-aware title bar button colors and
/// the application icon. Applies to both the main window and the gallery viewer window.
/// </summary>
internal static class Win11Chrome
{
    public static void Apply(Window window, FrameworkElement themeRoot)
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
        }

        var isDark = themeRoot.ActualTheme == ElementTheme.Dark;
        var titleBar = window.AppWindow.TitleBar;
        var foreground = isDark
            ? Color.FromArgb(255, 235, 235, 235)
            : Color.FromArgb(255, 20, 20, 20);
        var hover = isDark
            ? Color.FromArgb(60, 255, 255, 255)
            : Color.FromArgb(30, 0, 0, 0);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = hover;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(140, 235, 235, 235)
            : Color.FromArgb(140, 20, 20, 20);
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
