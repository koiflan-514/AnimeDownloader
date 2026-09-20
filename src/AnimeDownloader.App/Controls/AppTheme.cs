using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 控件主题查找。Avalonia 的样式分两处：<c>Application.Resources</c>（资源字典）
/// 与各 <c>Styles</c> 的 <c>Resources</c>（控件主题）。代码里要按 key 取一个
/// <see cref="ControlTheme"/> 时（例如 SegmentedControl 动态生成选项），
/// 用 <c>Application.Current.Resources[...]</c> 是取不到的 —— 主题住在 Styles 里。
///
/// 这个查找器同时覆盖两处，并在缺失时**立刻抛错**：主题名写错只会让控件
/// 退回 Fluent 默认皮肤（静默、且要到截图里才看得出来），把静默失败变成启动即失败。
/// </summary>
internal static class AppTheme
{
    /// <summary>按 key 取控件主题；不存在时抛出。</summary>
    public static ControlTheme Lookup(string key)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var value) && value is ControlTheme theme)
        {
            return theme;
        }

        throw new InvalidOperationException($"缺少控件主题：{key}");
    }
}
