namespace AnimeDownloader.App.Design;

/// <summary>
/// 界面图标。
/// </summary>
/// <remarks>
/// <para><b>与桌面端的关键差异</b>：WinUI / Avalonia 两个工程用的是 <c>Segoe Fluent Icons</c>
/// 私用区（PUA）字形（<c>&#xE711;</c> 之类），那套字体在 Android 上根本不存在。桌面端为此
/// 专门内置了一份等码位的开源替代字体 —— 但在 Android 上没必要重复这一路：</para>
///
/// <para>Android 自带字体族里就有一整套语义明确的<b>单色符号</b>（几何图形、箭头、 dingbats）。
/// 用它们有两个好处：不必往 APK 里塞字体（省 ~780KB），也不必维护「字形码位 ↔ 语义」的对照表 ——
/// 而这正是桌面端上一轮花了一整个会话去审计的那类 bug（PUA 码位不带语义，写错一位不报错、
/// 不缺字形、布局正常，只是画的不是那个东西）。</para>
///
/// <para><b>带 <c>\uFE0E</c> 的那几个不能省</b>：<c>⚙</c>(U+2699)、<c>⚠</c>(U+26A0) 在 Unicode
/// 里同时有「文字」和「emoji」两种呈现形式，Android 的系统字体默认可能选彩色 emoji 那一种 ——
/// 于是界面里会突然冒出一颗彩色的齿轮。追加 VARIATION SELECTOR-15 (U+FE0E) 是强制文字呈现的
/// 标准写法。不带 emoji 变体的字符（<c>▦</c>、<c>⤓</c>、<c>✕</c>）不需要这个后缀。</para>
/// </remarks>
internal static class DkIcons
{
    /// <summary>刷新 / 重新加载。</summary>
    internal const string Refresh = "\u21BB";

    /// <summary>下载 / 保存。</summary>
    internal const string Download = "\u2913";

    /// <summary>关闭 / 取消。</summary>
    internal const string Close = "\u2715";

    /// <summary>确认 / 完成。</summary>
    internal const string Check = "\u2713";

    /// <summary>设置。</summary>
    internal const string Settings = "\u2699\uFE0E";

    /// <summary>画廊 Tab（房屋轮廓，贴合设计稿底部导航）。</summary>
    internal const string Gallery = "\u2302";

    /// <summary>灯箱（大图查看）。</summary>
    internal const string Lightbox = "\u25A3";

    /// <summary>上一页。</summary>
    internal const string Previous = "\u2039";

    /// <summary>下一页。</summary>
    internal const string Next = "\u203A";

    /// <summary>返回（左尖括号，贴合设计稿顶部导航的小返回箭头）。</summary>
    internal const string Back = "\u2039";

    /// <summary>警告 / 故障。</summary>
    internal const string Warning = "\u26A0\uFE0E";

    /// <summary>清除。</summary>
    internal const string Clear = "\u232B\uFE0E";

    /// <summary>放大。</summary>
    internal const string ZoomIn = "\uFF0B";

    /// <summary>缩小。</summary>
    internal const string ZoomOut = "\u2212";

    /// <summary>适应屏幕。</summary>
    internal const string FitScreen = "\u2921";

    /// <summary>空状态图形：一块压暗的相纸。</summary>
    internal const string EmptyPaper = "\u25A2";

    /// <summary>“实际大小”不占图标 —— Segoe 与 Android 字体里都没有该语义的字形，
    /// 用等宽数字 <c>1:1</c> 零歧义，也正合「所有数字走等宽」这条规则。</summary>
    internal const string ActualSize = "1:1";

    // ---- 移动端重设计新增图标 ----

    /// <summary>搜索。</summary>
    internal const string Search = "\u2315";

    // 筛选与菜单不再走字形：设计稿里筛选是三线递减的滑杆、菜单是<b>两</b>条等长线，
    // 而系统字体只有「≡」这个等长三线的字形（会把两者画成同一个东西）。
    // 它们由 <see cref="Controls.LinesIconView"/> 直接绘制。

    /// <summary>更多（竖向）。</summary>
    internal const string MoreVertical = "\u22EE";

    /// <summary>分享。</summary>
    internal const string Share = "\u2197";

    /// <summary>收藏 / 喜欢。</summary>
    internal const string Heart = "\u2661";

    /// <summary>主页 / 首页（房屋轮廓，贴合设计稿底部 Tab）。</summary>
    internal const string Home = "\u2302";
}
