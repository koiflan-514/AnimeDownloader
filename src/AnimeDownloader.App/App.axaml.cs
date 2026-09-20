using AnimeDownloader.App.Platform;
using AnimeDownloader.Core.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AnimeDownloader.App;

/// <summary>
/// 应用入口：初始化设置存储、图源、主题，并创建主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>全局设置存储。</summary>
    public static SettingsStore SettingsStore { get; } = new();

    /// <summary>当前加载的设置（由主窗口持有并随设置页更新）。</summary>
    public static AppSettings Settings { get; set; } = SettingsStore.Load();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        HookCrashLogging();

        // 强调色必须在窗口显示之前落地一次：Tokens.axaml 里的字面量只是兜底，
        // 首帧若停在那个兜底蓝上，用户会看到一次「颜色闪一下」。
        // （该服务自己会订阅 IPlatformSettings.ColorValuesChanged 做实时跟随。）
        WindowsColorScheme.Instance.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 崩溃日志。对应 WinUI 版的 <c>Application.UnhandledException</c>：
    /// Avalonia 没有那一个入口，得同时挂住「UI 线程异常」「其它线程异常」
    /// 「后台任务未观察异常」三条路径，否则会漏掉一半。
    /// </summary>
    private static void HookCrashLogging()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            TryLog($"UIThread: {e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                TryLog($"AppDomain: {ex}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryLog($"Task: {e.Exception}");
        };
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
            // 日志写入失败不阻止崩溃处理
        }
    }

    /// <summary>应用请求的主题（跟随系统 / 浅色 / 深色）。</summary>
    public static ThemeVariant ResolveTheme(string theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
