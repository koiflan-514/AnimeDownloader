namespace AnimeDownloader.App.Views;

/// <summary>
/// 浏览器式页面（画廊 / 灯箱）的契约：挂到主窗口、响应图源与 NSFW 变化、
/// 支持外部触发重载（命令条上的刷新按钮）。
/// </summary>
public interface IModePage
{
    void Attach(MainWindow owner);

    void OnSourceChanged();

    void OnNsfwChanged();

    void Reload();
}

/// <summary>
/// 整窗全屏时页面收起 / 恢复自己的页内控件。外壳的导轨与命令条由 <see cref="MainWindow"/>
/// 自己处理；页面只负责自己那一层（例如查看器的浮动工具栏、状态条、标签区、内边距）。
/// </summary>
public interface IImmersivePage
{
    void SetImmersive(bool immersive);
}
