using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Xaml;

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

    private Window? _window;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTimeOffset.Now:O}] {e.Exception}\n");
            }
            catch
            {
                // 日志写入失败不阻止崩溃处理
            }
        };
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTimeOffset.Now:O}] OnLaunched: {ex}\n");
            }
            catch
            {
                // 忽略
            }

            throw;
        }
    }

    /// <summary>应用请求的主题（跟随系统 / 浅色 / 深色）。</summary>
    public static ElementTheme ResolveTheme(string theme) => theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
