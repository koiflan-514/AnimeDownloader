using Avalonia;

namespace AnimeDownloader.App;

/// <summary>
/// 进程入口。Avalonia 需要显式构建 AppBuilder —— 没有 WinUI 那种由生成代码注入的
/// Main，因此这里手工配置平台后端、日志与「先解析命令行再启动」的顺序。
/// </summary>
internal static class Program
{
    // Avalonia 的初始化必须早于任何 UI 类型被触碰：用 STAThread + 不引用控件的签名
    // 保证 JIT 不会在我们准备好之前把渲染栈带进来。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>供设计器与单元测试复用的构建方法（签名固定，不要改）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
