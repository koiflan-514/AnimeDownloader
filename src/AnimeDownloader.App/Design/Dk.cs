using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Util;
using AnimeDownloader.App.Platform;

namespace AnimeDownloader.App.Design;

/// <summary>应用主题模式，与设置页的「跟随系统 / 亮色 / 暗色」一一对应。</summary>
internal enum AppThemeMode
{
    /// <summary>跟随系统（默认）。</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// 暗房（Darkroom）设计令牌 —— Android 版。
/// </summary>
/// <remarks>
/// <para>概念与桌面端完全一致：中性冷调炭灰是「房间」，图片是唯一的光源，
/// <b>系统强调色</b>是暗房里的安全灯 —— 全应用唯一的彩色，只出现在三种语义上：
/// 正在被选中 / 正在进行 / 需要你注意。</para>
///
/// <para>三条硬规则（勿违背）：</para>
/// <list type="number">
///   <item><description>强调色 <b>绝不硬编码</b>。API 31+ 直接读系统的动态色
///   （Material You，<c>system_accent1_*</c>）；更低版本读主题的 <c>colorAccent</c>。
///   亮色模式下整体下沉一档 —— 系统强调色是为深色底挑的，直接压在白底上对比度不够。</description></item>
///   <item><description>层级<b>不用阴影</b>，只有表面色阶 + 1px 极细线。手机上尤其如此：
///   多余的高度/阴影在这里是纯粹的噪声。</description></item>
///   <item><description><b>所有数字走等宽</b>（页码、计数、分辨率、百分比、版本、路径）。
///   这条几乎不花成本，却立刻把界面从「网页」拉到「仪器」。</description></item>
/// </list>
/// </remarks>
internal sealed class Dk
{
    /// <summary>WCAG 相对亮度阈值：亮过它就压深墨，暗过它就压白墨。与桌面端同一常数。</summary>
    private const double InkLuminanceThreshold = 0.4;

    private readonly DisplayMetrics _metrics;

    private Dk(bool isLight, AccentScale accent, DisplayMetrics metrics, Typography typography)
    {
        IsLight = isLight;
        TypographyFor = typography;
        Accent = accent.Base;
        AccentStrong = accent.Strong;
        AccentSoft = accent.Soft;
        AccentLine = accent.Line;
        AccentInk = accent.Ink;
        _metrics = metrics;
    }

    /// <summary>当前是否亮色形态。</summary>
    internal bool IsLight { get; }

    /// <summary>当前尺寸档对应的字阶（由 <see cref="ScreenProfile"/> 解算）。</summary>
    internal Typography TypographyFor { get; }

    // ---------------- 表面色阶 ----------------

    /// <summary>房间底色（画布）。</summary>
    internal Color Background { get; private init; }

    /// <summary>导轨 / 底部导航（比画布略深，读作「墙」）。</summary>
    internal Color Rail { get; private init; }

    /// <summary>相纸 / 卡片。</summary>
    internal Color CardFill { get; private init; }

    /// <summary>悬停 / 按压抬升。</summary>
    internal Color CardHover { get; private init; }

    /// <summary>下沉面：输入框、分段器凹槽。</summary>
    internal Color Sunken { get; private init; }

    /// <summary>灯箱台面。<b>亮色模式下依然是深的</b> —— 看图需要中性深底衬，否则浅底会污染对图片明度的判断。</summary>
    internal Color ViewerBackground { get; private init; }

    /// <summary>唯一的「边框」语言。</summary>
    internal Color Hairline { get; private init; }

    /// <summary>分隔线强化 / 次按钮描边。</summary>
    internal Color HairlineStrong { get; private init; }

    /// <summary>悬停填充（只加白，不吃色相）。</summary>
    internal Color SubtleHover { get; private init; }

    /// <summary>按压填充。</summary>
    internal Color SubtlePressed { get; private init; }

    // ---------------- 文本四级 ----------------

    internal Color TextPrimary { get; private init; }

    internal Color TextSecondary { get; private init; }

    internal Color TextTertiary { get; private init; }

    internal Color TextQuaternary { get; private init; }

    // ---------------- 强调色（等于系统强调色） ----------------

    /// <summary>主操作填充、活动指示、忙碌状态。</summary>
    internal Color Accent { get; }

    /// <summary>悬停 / 按压。</summary>
    internal Color AccentStrong { get; }

    /// <summary>选中底衬（导轨项、分段器、标签 chip）。</summary>
    internal Color AccentSoft { get; }

    /// <summary>选中描边、卡片按压内描边。</summary>
    internal Color AccentLine { get; }

    /// <summary>压在强调色实心块上的文字 / 图标。</summary>
    internal Color AccentInk { get; }

    // ---------------- 状态三档 ----------------

    /// <summary>安静（无光源）。</summary>
    internal Color StatusIdle { get; private init; }

    /// <summary>工作（安全灯亮）。</summary>
    internal Color StatusBusy => Accent;

    /// <summary>故障。</summary>
    internal Color StatusError { get; private init; }

    // ---------------- 覆盖层 ----------------

    internal Color Smoke { get; private init; }

    internal Color DialogFill { get; private init; }

    internal Color DialogStroke { get; private init; }

    /// <summary>浮动工具条（玻璃药丸）的填充。</summary>
    internal Color ToolbarFill { get; private init; }

    /// <summary>标签 chip 的底色。</summary>
    internal Color TagChipFill { get; private init; }

    // ---------------- 字体族 ----------------

    // 下面四处末尾的 !：绑定把 Typeface.Create 标成可空返回（找不到字族时返回 null），
    // 但这几个字族是 Android 平台自带、必然存在的。令牌本身不该是可空类型 ——
    // 那会把空值判断扩散到每一个用到字体的控件里。

    /// <summary>标题与展板文字。</summary>
    internal Typeface FontDisplay { get; } = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal)!;

    /// <summary>正文与控件。</summary>
    internal Typeface FontText { get; } = Typeface.Create("sans-serif", TypefaceStyle.Normal)!;

    /// <summary>所有数字与元数据（本设计的签名细节）。</summary>
    internal Typeface FontMono { get; } = Typeface.Create("monospace", TypefaceStyle.Normal)!;

    /// <summary>等宽加粗：页码当前项、进度百分比。</summary>
    internal Typeface FontMonoStrong { get; } = Typeface.Create("monospace", TypefaceStyle.Bold)!;

    /// <summary>
    /// 按当前主题与系统强调色构造令牌。
    /// </summary>
    /// <param name="context">任意上下文（内部只用它的 Resources）。</param>
    /// <param name="mode">主题模式；<see cref="AppThemeMode.System"/> 时读系统夜间标志。</param>
    internal static Dk Create(Context context, AppThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resources = context.Resources
            ?? throw new InvalidOperationException("上下文没有 Resources");
        var isLight = mode switch
        {
            AppThemeMode.Light => true,
            AppThemeMode.Dark => false,
            _ => IsSystemLight(resources),
        };

        // 移动端重设计使用固定绿色强调色 #34C759，以保证三屏视觉一致。
        // 系统动态色逻辑仍保留在 DkAccent.Resolve 中，可按需要切换回去。
        var scale = DkAccent.Design();
        var metrics = resources.DisplayMetrics
            ?? throw new InvalidOperationException("上下文没有 DisplayMetrics");
        // 字阶随尺寸档变化（手机上的展板标题比平板小一号），所以令牌里也得带上它，
        // 否则每个控件都要自己去问一遍 Configuration。
        var typography = ScreenProfile.For(resources.Configuration!).Typography;

        return isLight
            ? CreateLight(scale, metrics, typography)
            : CreateDark(scale, metrics, typography);
    }

    /// <summary>系统当前是否处于亮色（夜间模式关闭）。</summary>
    internal static bool IsSystemLight(Resources resources)
    {
        var flags = resources.Configuration?.UiMode ?? UiMode.TypeUndefined;
        return (flags & UiMode.NightMask) != UiMode.NightYes;
    }

    private static Dk CreateDark(AccentScale accent, DisplayMetrics metrics, Typography typography) =>
        new(false, accent, metrics, typography)
    {
        // 重设计令牌：bg-base #0F0F10 / bg-surface #18181A / bg-elevated #232325 / border #2E2E30
        Background = Color.Argb(255, 0x0F, 0x0F, 0x10),
        Rail = Color.Argb(255, 0x18, 0x18, 0x1A),
        CardFill = Color.Argb(255, 0x18, 0x18, 0x1A),
        CardHover = Color.Argb(255, 0x23, 0x23, 0x25),
        Sunken = Color.Argb(255, 0x23, 0x23, 0x25),
        ViewerBackground = Color.Argb(255, 0x0F, 0x0F, 0x10),
        Hairline = Color.Argb(255, 0x2E, 0x2E, 0x30),
        HairlineStrong = Color.Argb(255, 0x3E, 0x3E, 0x42),
        SubtleHover = Color.Argb(0x0F, 0xFF, 0xFF, 0xFF),
        SubtlePressed = Color.Argb(0x1A, 0xFF, 0xFF, 0xFF),
        TextPrimary = Color.Argb(255, 0xF4, 0xF4, 0xF6),
        TextSecondary = Color.Argb(255, 0xA1, 0xA1, 0xAA),
        TextTertiary = Color.Argb(255, 0x71, 0x71, 0x7A),
        TextQuaternary = Color.Argb(255, 0x52, 0x52, 0x5A),
        StatusIdle = Color.Argb(255, 0x71, 0x71, 0x7A),
        StatusError = Color.Argb(255, 0xE0, 0x57, 0x5E),
        Smoke = Color.Argb(0x99, 0x0F, 0x0F, 0x10),
        DialogFill = Color.Argb(255, 0x18, 0x18, 0x1A),
        DialogStroke = Color.Argb(255, 0x3E, 0x3E, 0x42),
        ToolbarFill = Color.Argb(0xF2, 0x18, 0x18, 0x1A),
        TagChipFill = Color.Argb(255, 0x23, 0x23, 0x25),
    };

    private static Dk CreateLight(AccentScale accent, DisplayMetrics metrics, Typography typography) =>
        new(true, accent, metrics, typography)
    {
        Background = Color.Argb(255, 0xF7, 0xF7, 0xF9),
        Rail = Color.Argb(255, 0xF1, 0xF1, 0xF4),
        CardFill = Color.Argb(255, 0xFF, 0xFF, 0xFF),
        CardHover = Color.Argb(255, 0xFB, 0xFB, 0xFD),
        Sunken = Color.Argb(255, 0xEB, 0xEB, 0xEF),
        // 灯箱在亮色下依旧是深的 —— 这是本设计里唯一「故意不统一」的地方。
        ViewerBackground = Color.Argb(255, 0x16, 0x17, 0x1B),
        Hairline = Color.Argb(0x12, 0x00, 0x00, 0x00),
        HairlineStrong = Color.Argb(0x21, 0x00, 0x00, 0x00),
        SubtleHover = Color.Argb(0x0A, 0x00, 0x00, 0x00),
        SubtlePressed = Color.Argb(0x14, 0x00, 0x00, 0x00),
        TextPrimary = Color.Argb(255, 0x15, 0x16, 0x1A),
        TextSecondary = Color.Argb(255, 0x5A, 0x60, 0x6A),
        TextTertiary = Color.Argb(255, 0x87, 0x8D, 0x96),
        TextQuaternary = Color.Argb(255, 0xB3, 0xB7, 0xBE),
        StatusIdle = Color.Argb(255, 0x9A, 0xA0, 0xA8),
        StatusError = Color.Argb(255, 0xC0, 0x39, 0x2F),
        Smoke = Color.Argb(0x66, 0x00, 0x00, 0x00),
        DialogFill = Color.Argb(255, 0xFF, 0xFF, 0xFF),
        DialogStroke = Color.Argb(0x21, 0x00, 0x00, 0x00),
        ToolbarFill = Color.Argb(0xF2, 0xFF, 0xFF, 0xFF),
        TagChipFill = Color.Argb(0x0A, 0x00, 0x00, 0x00),
    };

    // ---------------- 单位换算 ----------------

    /// <summary>dp → px。</summary>
    internal float Dp(float dp) => dp * _metrics.Density;

    /// <summary>dp → px（整数，用于画线、定尺寸这些不该有小数的地方）。</summary>
    internal int Dpi(float dp) => (int)Math.Round(dp * _metrics.Density);

    /// <summary>
    /// sp → px，随系统字体缩放。
    /// </summary>
    /// <remarks>
    /// 走 <c>TypedValue.ApplyDimension</c> 而不是 <c>metrics.ScaledDensity</c>：后者自 API 34 起废弃
    /// （非线性字体缩放之后，它不再等于 density × fontScale），前者的换算在两个时代都正确。
    /// 正文、标签、按钮文字一律走这里 —— 尊重用户的系统字号是可达性的底线要求。
    /// </remarks>
    internal float Sp(float sp) =>
        TypedValue.ApplyDimension(ComplexUnitType.Sp, sp, _metrics);

    /// <summary>
    /// sp → px，但带一个上限。
    /// </summary>
    /// <remarks>
    /// 只用在**展板标题**这类大字号上：用户把系统字号调到 2.0× 时，32sp 的标题会变成 64sp，
    /// 一屏只剩标题。给大字号加上限、让正文继续自由缩放，是「响应式」与「可达性」之间
    /// 唯一说得通的折中 —— 需要看清的内容（正文、标签、数字）放大不受影响。
    /// </remarks>
    internal float SpCapped(float sp, float maxSp) => Math.Min(Sp(sp), Sp(maxSp));

    /// <summary>极细线宽度：1dp，且至少 1 物理像素（否则在高密度屏上会被画没）。</summary>
    internal float HairlineWidth => Math.Max(1f, Dp(1f));

    /// <summary>最小触摸目标（dp）。48dp 是 Android 无障碍规范的硬指标。</summary>
    internal static float TouchTargetDp => ScreenProfile.MinimumTouchTargetDp;

    /// <summary>最小触摸目标（px）。低于它手指点不中，是无障碍审计的硬指标。</summary>
    internal float TouchTarget => Dp(TouchTargetDp);
}

/// <summary>系统强调色的派生色阶。</summary>
/// <param name="Base">主色。</param>
/// <param name="Strong">悬停 / 按压色。</param>
/// <param name="Soft">底衬（半透明）。</param>
/// <param name="Line">描边（半透明）。</param>
/// <param name="Ink">压在主色上的文字色。</param>
internal readonly record struct AccentScale(Color Base, Color Strong, Color Soft, Color Line, Color Ink);

/// <summary>
/// 读取系统强调色并派生整套色阶。
/// </summary>
/// <remarks>
/// <para><b>这是桌面端 <c>WindowsColorScheme</c> 在 Android 上的对应物。</b>
/// Windows 读注册表的 <c>AccentPalette</c>，Android 则有两档：</para>
/// <list type="bullet">
///   <item><description><b>API 31+</b>：直接读系统的动态配色资源
///   （<c>system_accent1_500</c> 等）。这就是 Material You —— 用户换壁纸，系统会据此
///   重新生成整套色阶，我们只要跟着取。</description></item>
///   <item><description><b>API 24–30</b>：没有动态配色，读主题的 <c>colorAccent</c>
///   （由 <c>AppTheme</c> 提供）。语义上等价于 Windows 的「个性化颜色」。</description></item>
/// </list>
/// <para>两档都拿不到时才回落到兜底蓝色。整条路径上没有任何一处硬编码的品牌色 ——
/// 这是设计规范里写死的规则：应用不该有自己的颜色。</para>
/// </remarks>
internal static class DkAccent
{
    /// <summary>兜底色（= colors.xml 里的 dk_accent_fallback，也就是 Windows 默认强调色）。</summary>
    private static readonly Color Fallback = Color.Argb(255, 0x00, 0x78, 0xD4);

    /// <summary>底衬透明度：暗色 16%。</summary>
    private const float SoftAlphaDark = 0.16f;

    /// <summary>底衬透明度：亮色 12%（浅底上 16% 会糊成一团灰）。</summary>
    private const float SoftAlphaLight = 0.12f;

    private const float LineAlphaDark = 0.42f;

    private const float LineAlphaLight = 0.45f;

    /// <summary>重设计固定强调色 #34C759 的完整色阶。</summary>
    internal static AccentScale Design()
    {
        var baseColor = Color.Argb(255, 0x34, 0xC7, 0x59);
        return new AccentScale(
            baseColor,
            Adjust(baseColor, 0.18f),
            WithAlpha(baseColor, 0.15f),
            WithAlpha(baseColor, 0.50f),
            InkFor(baseColor));
    }

    /// <summary>读取当前系统强调色并派生色阶（按当前是否亮色选用对应的透明度档）。</summary>
    internal static AccentScale Resolve(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isLight = Dk.IsSystemLight(context.Resources!);
        var baseColor = ReadSystemAccent(context);

        // 亮色模式：整体下沉一档。系统强调色是为深色底挑的值，
        // 直接当文字或描边压在白底上会偏亮、对比不足 —— 这也是 WinUI 自己的处理方式。
        var strong = Adjust(baseColor, isLight ? -0.22f : 0.18f);
        var softAlpha = isLight ? SoftAlphaLight : SoftAlphaDark;
        var lineAlpha = isLight ? LineAlphaLight : LineAlphaDark;

        return new AccentScale(
            baseColor,
            strong,
            WithAlpha(baseColor, softAlpha),
            WithAlpha(baseColor, lineAlpha),
            InkFor(baseColor));
    }

    private static Color ReadSystemAccent(Context context)
    {
        var resources = context.Resources!;
        var theme = context.Theme;

        // API 31+：Material You 动态配色。
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var dynamic_ = TryGetColor(resources, theme, Android.Resource.Color.SystemAccent1500);
            if (dynamic_ is { } color)
            {
                return color;
            }
        }

        // 兜底一：主题里声明的 colorAccent。
        var fromTheme = ReadThemeAccent(context);
        if (fromTheme is { } themed)
        {
            return themed;
        }

        // 兜底二：本应用的颜色资源。
        return TryGetColor(resources, theme, Resource.Color.dk_accent_fallback) ?? Fallback;
    }

    private static Color? ReadThemeAccent(Context context)
    {
        var theme = context.Theme;
        if (theme is null)
        {
            return null;
        }

        try
        {
            using var values = theme.ObtainStyledAttributes([Android.Resource.Attribute.ColorAccent]);
            if (values is null || !values.HasValue(0))
            {
                return null;
            }

            var color = values.GetColor(0, Fallback);
            // 取出来的可能是全透明（某些主题没设 colorAccent），那不是有效强调色。
            return color.A == 0 ? null : color;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Color? TryGetColor(Resources resources, Resources.Theme? theme, int resourceId)
    {
        try
        {
            return resources.GetColor(resourceId, theme);
        }
        catch (Exception)
        {
            // 资源不存在（低版本系统）或读取被拒：交给下一档兜底。
            return null;
        }
    }

    /// <summary>
    /// 墨色自动判定：用 WCAG 相对亮度决定压深墨还是白墨。
    /// </summary>
    /// <remarks>
    /// 这样无论用户选的是明黄、薄荷还是深靛，实心按钮上的文字都保持可读，
    /// 不需要为每个色相手工配一个墨色常数。阈值 0.4 与桌面端一致 ——
    /// 同一个明黄在深色下会取深墨、在亮色下因为先下沉一档又取回白墨，
    /// 一个颜色、两种底色、两套正确解。
    /// </remarks>
    internal static Color InkFor(Color color)
    {
        static double Channel(int value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Channel(color.R))
            + (0.7152 * Channel(color.G))
            + (0.0722 * Channel(color.B));

        return luminance > 0.4
            ? Color.Argb(255, 0x14, 0x15, 0x18)
            : Color.Argb(255, 0xFF, 0xFF, 0xFF);
    }

    private static Color WithAlpha(Color color, float alpha) =>
        Color.Argb((int)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255), color.R, color.G, color.B);

    /// <summary>按比例提亮（delta &gt; 0）或压深（delta &lt; 0），保持色相。</summary>
    private static Color Adjust(Color color, float delta)
    {
        static int Shift(int channel, float amount) => amount >= 0
            ? (int)Math.Round(channel + ((255 - channel) * amount))
            : (int)Math.Round(channel * (1 + amount));

        return Color.Argb(
            color.A,
            Math.Clamp(Shift(color.R, delta), 0, 255),
            Math.Clamp(Shift(color.G, delta), 0, 255),
            Math.Clamp(Shift(color.B, delta), 0, 255));
    }

    /// <summary>把 DP 换算成 sp 再转 px 的便捷入口（供纯逻辑层复用）。</summary>
    internal static float SpToPx(Context context, float sp) =>
        TypedValue.ApplyDimension(ComplexUnitType.Sp, sp, context.Resources!.DisplayMetrics!);
}
