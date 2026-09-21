using Android.Content.Res;

namespace AnimeDownloader.App.Platform;

/// <summary>窗口尺寸档。按<b>最短边</b>判定 —— 这样横竖屏切换不会让「这是手机还是平板」的结论翻转。</summary>
internal enum SizeClass
{
    /// <summary>手机（最短边 &lt; 600dp）。</summary>
    Compact,

    /// <summary>小平板 / 折叠屏展开态（600–839dp）。</summary>
    Medium,

    /// <summary>平板 / 桌面模式（≥ 840dp）。</summary>
    Expanded,
}

/// <summary>导航形态。</summary>
internal enum NavigationStyle
{
    /// <summary>底部导航栏：手机上的默认形态。</summary>
    BottomBar,

    /// <summary>左侧导轨 56dp：桌面端原版形态，在大屏与横屏手机上更合适。</summary>
    Rail,
}

/// <summary>
/// 一整套字阶。字号全部是 <b>sp</b>（随系统字号缩放），单位换算由 <see cref="Design.Dk"/> 负责。
/// </summary>
/// <param name="Display">展板标题（画廊标题）。</param>
/// <param name="SectionTitle">区块标题（灯箱 / 设置页）。</param>
/// <param name="CardTitle">卡片、区块小标题。</param>
/// <param name="Body">正文。</param>
/// <param name="Hint">副标题、说明。</param>
/// <param name="Label">表单字段标签。</param>
/// <param name="Caption">状态、页脚、元数据（等宽）。</param>
internal readonly record struct Typography(
    float Display,
    float SectionTitle,
    float CardTitle,
    float Body,
    float Hint,
    float Label,
    float Caption);

/// <summary>
/// 响应式布局的解算结果：尺寸档 → 导航形态、字阶、网格栏距。
/// </summary>
/// <remarks>
/// <para><b>为什么要有这一层，而不是到处写 <c>if (width &gt; x)</c></b>：
/// 断点一旦分散在十几个控件里，就会出现「底部栏按手机算、网格按平板算」这类自相矛盾的状态。
/// 把它们收敛成一处纯计算，既好审也<b>可单测</b>（见 Download.Tests 里的同类做法）。</para>
///
/// <para><b>网格列数不从这里出</b>：列数取决于「内容区实际可用宽度」（还要扣掉导轨与内边距），
/// 那是布局时刻才知道的值。这里只给出<b>理想卡片边长与栏距</b>，列数由
/// <see cref="ResolveColumns"/> 现场算。</para>
/// </remarks>
/// <param name="Class">尺寸档。</param>
/// <param name="Navigation">导航形态。</param>
/// <param name="Typography">字阶。</param>
/// <param name="IdealCardSizeDp">理想卡片边长（dp）。</param>
/// <param name="CardGutterDp">卡片栏距（dp）。</param>
/// <param name="MinColumns">最少列数。</param>
/// <param name="MaxColumns">最多列数。</param>
/// <param name="TouchTargetDp">最小触摸目标（dp）。</param>
internal readonly record struct ScreenProfile(
    SizeClass Class,
    NavigationStyle Navigation,
    Typography Typography,
    float IdealCardSizeDp,
    float CardGutterDp,
    int MinColumns,
    int MaxColumns,
    float TouchTargetDp)
{
    /// <summary>最小的触摸目标：48dp（Android 无障碍规范的硬指标）。</summary>
    internal const float MinimumTouchTargetDp = 48f;

    /// <summary>按当前配置解算。</summary>
    internal static ScreenProfile For(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var widthDp = configuration.ScreenWidthDp;
        var heightDp = configuration.ScreenHeightDp;
        // SmallestScreenWidthDp 是系统给的「这台设备有多宽」的稳定指标（不随旋转变化），
        // 但它在某些多窗口场景下会是 0，所以退回用宽高较小者。
        var smallest = configuration.SmallestScreenWidthDp;
        if (smallest <= 0)
        {
            smallest = Math.Min(widthDp, heightDp);
        }

        var sizeClass = smallest switch
        {
            < 600 => SizeClass.Compact,
            < 840 => SizeClass.Medium,
            _ => SizeClass.Expanded,
        };

        return For(sizeClass, widthDp, heightDp);
    }

    /// <summary>按尺寸档与当前宽高解算（抽出来是为了可测：不依赖 Configuration 对象）。</summary>
    internal static ScreenProfile For(SizeClass sizeClass, int widthDp, int heightDp)
    {
        // 横屏手机上「矮」是主要矛盾：800×360 这种窗口里，底部 64dp 导航栏会直接
        // 吃掉一行图的高度。所以宽而矮的窗口改用左侧导轨 —— 它占的是本来就富余的横向空间。
        var wideButShort = widthDp >= 720 && heightDp < 480;
        var navigation = sizeClass != SizeClass.Compact || wideButShort
            ? NavigationStyle.Rail
            : NavigationStyle.BottomBar;

        return sizeClass switch
        {
            SizeClass.Expanded => new ScreenProfile(
                sizeClass,
                navigation,
                // 平板：桌面端原版字阶
                new Typography(32f, 26f, 14f, 13f, 12.5f, 11.5f, 11.5f),
                238f,
                10f,
                MinColumns: 4,
                MaxColumns: 8,
                MinimumTouchTargetDp),
            SizeClass.Medium => new ScreenProfile(
                sizeClass,
                navigation,
                new Typography(28f, 23f, 14f, 13f, 12.5f, 11.5f, 11.5f),
                180f,
                10f,
                MinColumns: 3,
                MaxColumns: 6,
                MinimumTouchTargetDp),
            _ => new ScreenProfile(
                sizeClass,
                navigation,
                // 手机：展板标题从 32 收到 24 —— 竖直方向是稀缺资源，
                // 大标题在 360dp 宽的屏上会占掉整整两行。
                new Typography(24f, 20f, 14f, 13.5f, 12.5f, 11.5f, 11.5f),
                152f,
                8f,
                MinColumns: 2,
                MaxColumns: 5,
                MinimumTouchTargetDp),
        };
    }

    /// <summary>
    /// 按可用宽度算列数。
    /// </summary>
    /// <remarks>
    /// 注意这里<b>不</b>做「列宽 = (宽 − 栏距×(n−1)) / n」那种预留式算法。桌面端踩过这个坑：
    /// 那样算出来预留的栏距会变成右侧空掉的一条，而卡片之间反而贴死。
    /// 正确规则是：<b>瓦片 = 行宽 ÷ 列数（精确铺满），卡片 = 瓦片 − 栏距并居中</b>，
    /// 于是相邻两张之间自然留出栏距，最外两侧各让出栏距的一半。
    /// </remarks>
    internal int ResolveColumns(float availableWidthDp)
    {
        if (availableWidthDp <= 0)
        {
            return MinColumns;
        }

        var columns = (int)Math.Round(availableWidthDp / (IdealCardSizeDp + CardGutterDp));
        return Math.Clamp(columns, MinColumns, MaxColumns);
    }

    /// <summary>
    /// 算出瓦片边长（px）。
    /// </summary>
    /// <param name="availableWidthPx">内容区可用宽度（px）。</param>
    /// <param name="columns">列数。</param>
    /// <param name="gutterPx">栏距（px）。</param>
    /// <remarks>
    /// 瓦片取 <c>Math.Floor</c>（再留半像素余量）这一步不能省：面板拿到的往往是带小数的
    /// 可用宽度，瓦片若取除法原值，n 个瓦片会刚好等于可用宽度而攒出一次多余的换行 ——
    /// 症状是<b>最右一列整列消失</b>（每行只排满 n−1 张），极易误判成「列数算错」。
    /// </remarks>
    internal static float ResolveTileSize(float availableWidthPx, int columns, float gutterPx)
    {
        if (columns <= 0)
        {
            return 0;
        }

        var tile = (float)Math.Floor((availableWidthPx - 0.5f) / columns);
        return Math.Max(gutterPx + 1f, tile);
    }
}
