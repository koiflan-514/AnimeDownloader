using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AnimeDownloader.App.Design;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 1 物理像素的极细线 —— 本设计里唯一的「边框」语言。
/// </summary>
/// <remarks>
/// 刻意<b>关掉抗锯齿</b>：一条 1px 的线开了抗锯齿会被摊成两行 50% 灰，看起来就是「虚的」。
/// 同理用 <see cref="Canvas.DrawRect(float, float, float, float, Paint)"/> 而不是画线 ——
/// 线的端点与半边像素对齐问题在极细线上会被放大成可见的断点。
/// </remarks>
internal sealed class HairlineView : View
{
    private readonly Paint _paint = new() { AntiAlias = false };

    internal HairlineView(Context context, Dk dk, bool vertical = false)
        : base(context)
    {
        Vertical = vertical;
        _paint.Color = dk.Hairline;
    }

    /// <summary>true 为竖线（宽 1px、撑满高度），false 为横线（高 1px、撑满宽度）。</summary>
    internal bool Vertical { get; }

    /// <summary>线条颜色。</summary>
    internal Color LineColor
    {
        get => _paint.Color;
        set
        {
            if (_paint.Color != value)
            {
                _paint.Color = value;
                Invalidate();
            }
        }
    }

    /// <summary>线宽（px）。默认 1 物理像素。</summary>
    internal int Thickness { get; init; } = 1;

    protected override void OnDraw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        base.OnDraw(canvas);

        if (Vertical)
        {
            canvas.DrawRect(0, 0, Thickness, Height, _paint);
        }
        else
        {
            canvas.DrawRect(0, 0, Width, Thickness, _paint);
        }
    }
}

/// <summary>
/// 状态点：三档语义的唯一指示器。
/// </summary>
/// <remarks>
/// 灰 = 安静（无光源）/ 强调色 = 工作（安全灯亮）/ 红 = 故障。
/// 三档互斥且优先级固定（故障 &gt; 工作），所以对外只暴露一个
/// <see cref="Set"/> 而不是三个独立开关 —— 后者迟早会被调用方设成「又忙又错」的矛盾状态。
/// </remarks>
internal sealed class StatusDotView : View
{
    private const float DotSizeDp = 8f;

    private readonly Paint _paint = new() { AntiAlias = true };
    private readonly Dk _dk;

    internal StatusDotView(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        _paint.Color = dk.StatusIdle;
        SetWillNotDraw(false);
    }

    /// <summary>按语义设置状态。error 优先于 busy（同时成立时显示故障）。</summary>
    internal void Set(bool busy = false, bool error = false)
    {
        var color = error ? _dk.StatusError : busy ? _dk.StatusBusy : _dk.StatusIdle;
        if (_paint.Color != color)
        {
            _paint.Color = color;
            Invalidate();
        }
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var size = _dk.Dpi(DotSizeDp);
        SetMeasuredDimension(
            MeasureSpecs.Resolve(size, widthMeasureSpec, size),
            MeasureSpecs.Resolve(size, heightMeasureSpec, size));
    }

    protected override void OnDraw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        base.OnDraw(canvas);

        var radius = Math.Min(Width, Height) / 2f;
        canvas.DrawCircle(Width / 2f, Height / 2f, radius, _paint);
    }
}

/// <summary>
/// 相纸 / 面板：圆角表面 + 1px 极细线。
/// </summary>
/// <remarks>
/// 层级<b>不用阴影</b>，只用表面色阶 + 细线 —— 暗房里没有投影，只有离灯远近不同的灰。
/// 因此这类容器一律 <c>Elevation = 0</c>（Android 默认会给一点高度，必须显式归零）。
/// </remarks>
internal sealed class PaperView : FrameLayout
{
    private readonly Dk _dk;

    internal PaperView(Context context, Dk dk, float cornerDp = 12f)
        : base(context)
    {
        _dk = dk;
        Elevation = 0f;
        Apply(dk.CardFill, true, cornerDp);
    }

    /// <summary>重设表面：填充色、是否描边、圆角。</summary>
    internal void Apply(Color fill, bool bordered, float cornerDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetCornerRadius(_dk.Dp(cornerDp));
        drawable.SetColor(fill);
        if (bordered)
        {
            drawable.SetStroke(_dk.Dpi(1f), _dk.Hairline);
        }

        Background = drawable;
    }
}

/// <summary>
/// 2dp 的细进度条。强调色填充 + 下沉轨道，没有圆角胶囊、没有动画光带。
/// </summary>
internal sealed class ThinProgressBar : View
{
    private const float BarHeightDp = 2f;

    private readonly Paint _track = new() { AntiAlias = true };
    private readonly Paint _fill = new() { AntiAlias = true };
    private readonly Dk _dk;
    private double _progress;

    internal ThinProgressBar(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        _track.Color = dk.Sunken;
        _fill.Color = dk.StatusBusy;
        SetWillNotDraw(false);
    }

    /// <summary>进度 0–1。</summary>
    internal double Progress
    {
        get => _progress;
        set
        {
            var clamped = Math.Clamp(value, 0d, 1d);
            if (Math.Abs(clamped - _progress) > 0.0005)
            {
                _progress = clamped;
                Invalidate();
            }
        }
    }

    /// <summary>true 时显示为不确定进度（一条低对比的微光扫过，而不是旋转圈）。</summary>
    internal bool Indeterminate { get; set; }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var height = _dk.Dpi(BarHeightDp);
        SetMeasuredDimension(
            MeasureSpecs.Resolve(0, widthMeasureSpec, Width),
            MeasureSpecs.Resolve(height, heightMeasureSpec, height));
    }

    protected override void OnDraw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        base.OnDraw(canvas);

        var half = Height / 2f;
        canvas.DrawRect(0, 0, Width, Height, _track);
        var filled = Indeterminate ? Width * 0.25f : (float)(Width * _progress);
        if (filled > 0)
        {
            canvas.DrawRect(0, 0, filled, Height, _fill);
        }

        // 轨道两端各留一点圆头，避免细线在圆角容器里显得突兀。
        if (half > 0 && Width > Height)
        {
            canvas.DrawCircle(half, half, half, _track);
        }
    }
}

/// <summary>
/// 空 / 错 / 忙三态的统一呈现：一块压暗的相纸 + 一句直接陈述 + 一个出口。
/// </summary>
/// <remarks>
/// 文案直接说「下一步做什么」（「换一批看看」「加载失败：…」），不用「哎呀」这类拟声词，
/// 也不用「暂无数据」这种什么都没说的占位。
/// </remarks>
internal sealed class EmptyStateView : LinearLayout
{
    private readonly Dk _dk;
    private readonly TextView _glyph;
    private readonly TextView _title;
    private readonly TextView _message;
    private readonly DkButton _action;

    internal EmptyStateView(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        // 类型名必须全限定：本类继承自 LinearLayout，裸写 Orientation 会命中那个实例属性（CS0176）。
        Orientation = Android.Widget.Orientation.Vertical;
        // 同理 Gravity 是只读属性，只能走 SetGravity。
        SetGravity(GravityFlags.Center);
        Visibility = ViewStates.Gone;
        SetPadding(dk.Dpi(24f), dk.Dpi(40f), dk.Dpi(24f), dk.Dpi(40f));

        _glyph = new TextView(context)
        {
            Text = DkIcons.EmptyPaper,
            TextSize = 34f,
            Gravity = GravityFlags.Center,
        };
        _glyph.SetTextColor(dk.TextQuaternary);
        AddView(_glyph, LayoutParams.WrapContent, LayoutParams.WrapContent);

        _title = new TextView(context)
        {
            TextSize = dk.TypographyFor.CardTitle,
            Gravity = GravityFlags.Center,
        };
        _title.SetTypeface(dk.FontDisplay, TypefaceStyle.Normal);
        _title.SetTextColor(dk.TextPrimary);
        var titleParams = new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(14f),
        };
        AddView(_title, titleParams);

        _message = new TextView(context)
        {
            TextSize = dk.TypographyFor.Hint,
            Gravity = GravityFlags.Center,
        };
        _message.SetTextColor(dk.TextSecondary);
        var messageParams = new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(6f),
        };
        AddView(_message, messageParams);

        _action = new DkButton(context, dk, DkButtonVariant.Pill)
        {
            Text = "换一批看看",
        };
        var actionParams = new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(18f),
        };
        AddView(_action, actionParams);
        _action.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户点了「重试 / 换一批」。</summary>
    internal event EventHandler? RetryRequested;

    /// <summary>显示空状态。</summary>
    internal void Show(string title, string message, string actionText = "换一批看看")
    {
        _title.Text = title;
        _message.Text = message;
        _action.Text = actionText;
        _action.Visibility = string.IsNullOrEmpty(actionText) ? ViewStates.Gone : ViewStates.Visible;
        Visibility = ViewStates.Visible;
    }

    /// <summary>隐藏。</summary>
    internal void Hide() => Visibility = ViewStates.Gone;
}

/// <summary>按钮形态。</summary>
internal enum DkButtonVariant
{
    /// <summary>强调色实心：整页只应出现一次的主操作。</summary>
    Accent,

    /// <summary>透明底 + 1px 细线：次操作。</summary>
    Pill,

    /// <summary>纯文字：第三级操作。</summary>
    Ghost,

    /// <summary>故障语气的文字按钮。</summary>
    Danger,
}

/// <summary>
/// 暗房按钮。
/// </summary>
/// <remarks>
/// <para>三条与「手机专门优化」直接相关的规则，全部写在这里，控件之外不必再想：</para>
/// <list type="number">
///   <item><description><b>最小高度 48dp</b>（<see cref="Platform.ScreenProfile.MinimumTouchTargetDp"/>）。
///   手指的接触面远大于鼠标指针，低于这个尺寸的按钮在真机上会「点了没反应」。</description></item>
///   <item><description><b>按压反馈用涟漪而不是缩放</b>。触摸界面里用户看不到指针，
///   涟漪是唯一能说明「这一下点到了」的信号。</description></item>
///   <item><description><b>文字走 sp</b>，随系统字号缩放；按钮高度是
///   <c>max(48dp, 文字高 + 内边距)</c>，所以字号放大时按钮跟着长高，而不是把文字裁掉。</description></item>
/// </list>
/// </remarks>
internal sealed class DkButton : TextView
{
    private readonly Dk _dk;
    private readonly DkButtonVariant _variant;

    internal DkButton(Context context, Dk dk, DkButtonVariant variant)
        : base(context)
    {
        _dk = dk;
        _variant = variant;
        Elevation = 0f;
        Gravity = GravityFlags.Center;
        SetMinHeight(dk.Dpi(Dk.TouchTargetDp));
        SetMinWidth(dk.Dpi(Dk.TouchTargetDp));
        SetPadding(dk.Dpi(16f), dk.Dpi(10f), dk.Dpi(16f), dk.Dpi(10f));
        TextSize = dk.TypographyFor.Label;
        ApplyVariant();
    }

    /// <summary>把当前变体的外观重新套一遍（主题切换 / 状态变化后调用）。</summary>
    internal void ApplyVariant()
    {
        const float corner = 7f;
        Color fill;
        Color? stroke = null;
        Color text;

        switch (_variant)
        {
            case DkButtonVariant.Accent:
                fill = _dk.Accent;
                text = _dk.AccentInk;
                break;
            case DkButtonVariant.Pill:
                fill = Color.Transparent;
                stroke = _dk.HairlineStrong;
                text = _dk.TextPrimary;
                break;
            case DkButtonVariant.Danger:
                fill = Color.Transparent;
                stroke = _dk.HairlineStrong;
                text = _dk.StatusError;
                break;
            default:
                fill = Color.Transparent;
                text = _dk.TextSecondary;
                break;
        }

        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(_dk.Dp(corner));
        shape.SetColor(fill);
        if (stroke is { } strokeColor)
        {
            shape.SetStroke(_dk.Dpi(1f), strokeColor);
        }

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(_dk.Dp(corner));
        mask.SetColor(Color.White);

        var rippleColor = _variant == DkButtonVariant.Accent
            ? _dk.AccentInk
            : _dk.Accent;
        Background = new RippleDrawable(RippleColor(rippleColor, 0x38), shape, mask);

        SetTextColor(text);
        SetTypeface(_dk.FontText, TypefaceStyle.Normal);
    }

    /// <summary>方形图标按钮（48×48dp，字形居中）。</summary>
    internal static DkButton Icon(Context context, Dk dk, DkButtonVariant variant = DkButtonVariant.Ghost)
    {
        var button = new DkButton(context, dk, variant)
        {
            TextSize = 16f,
        };
        var size = dk.Dpi(Dk.TouchTargetDp);
        button.SetMinWidth(size);
        button.SetMinimumWidth(size);
        button.SetPadding(0, 0, 0, 0);
        button.Gravity = GravityFlags.Center;
        return button;
    }

    internal static ColorStateList RippleColor(Color color, int alpha)
    {
        var value = Color.Argb(alpha, color.R, color.G, color.B);
        // 空状态集用 Array.Empty 而不是 new int[0]：后者每次调用都真的分配一个数组
        // （ColorStateList 关心的只是「没有额外状态」这件事）。
        return new ColorStateList([Array.Empty<int>()], [value]);
    }
}

/// <summary>
/// 细线分段器：容器只是凹槽，选中项用强调色底衬 + 强调色描边，未选中项完全透明。
/// </summary>
/// <remarks>
/// 手机上把分段器高度定在 40dp（低于 48dp 的触摸目标下限）是<b>可以</b>的 ——
/// 前提是整个凹槽的高度撑到 48dp 并让分段项填满它。这里正是这么做的：
/// <c>SetMinimumHeight</c> 取 48dp，分段项自己填满剩余空间，
/// 于是「看起来紧凑、点起来够大」两件事同时成立。
/// </remarks>
internal sealed class SegmentedControl : LinearLayout
{
    private readonly Dk _dk;
    private readonly List<TextView> _segments = [];
    private int _selectedIndex;

    internal SegmentedControl(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        Orientation = Android.Widget.Orientation.Horizontal;
        Elevation = 0f;
        SetMinimumHeight(dk.Dpi(Dk.TouchTargetDp));

        var groove = new GradientDrawable();
        groove.SetShape(ShapeType.Rectangle);
        groove.SetCornerRadius(dk.Dp(7f));
        groove.SetColor(dk.Sunken);
        groove.SetStroke(dk.Dpi(1f), dk.Hairline);
        Background = groove;

        var padding = dk.Dpi(2f);
        SetPadding(padding, padding, padding, padding);
    }

    /// <summary>选中项下标变化。</summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>当前选中项下标。</summary>
    internal int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _segments.Count - 1));
            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            Refresh();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>设置分段项。会重建子视图。</summary>
    internal void SetSegments(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        RemoveAllViews();
        _segments.Clear();

        foreach (var label in labels)
        {
            var index = _segments.Count;
            var segment = new TextView(Context!)
            {
                Text = label,
                Gravity = GravityFlags.Center,
                TextSize = _dk.TypographyFor.Label,
            };
            segment.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
            segment.SetMinHeight(_dk.Dpi(40f));
            segment.Click += (_, _) =>
            {
                SelectedIndex = index;
                // 手动触发：SelectedIndex 的 setter 在值未变时会提前返回，
                // 而用户重复点同一项也应该收到反馈（例如用当前段重新加载）。
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };

            var parameters = new LayoutParams(0, LayoutParams.MatchParent, 1f);
            AddView(segment, parameters);
            _segments.Add(segment);
        }

        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _segments.Count - 1));
        Refresh();
    }

    private void Refresh()
    {
        for (var i = 0; i < _segments.Count; i++)
        {
            var selected = i == _selectedIndex;
            var shape = new GradientDrawable();
            shape.SetShape(ShapeType.Rectangle);
            shape.SetCornerRadius(_dk.Dp(5f));
            shape.SetColor(selected ? _dk.AccentSoft : Color.Transparent);
            if (selected)
            {
                shape.SetStroke(_dk.Dpi(1f), _dk.AccentLine);
            }

            var mask = new GradientDrawable();
            mask.SetShape(ShapeType.Rectangle);
            mask.SetCornerRadius(_dk.Dp(5f));
            mask.SetColor(Color.White);

            _segments[i].Background = new RippleDrawable(
                DkButton.RippleColor(_dk.Accent, 0x2E),
                shape,
                mask);
            _segments[i].SetTextColor(selected ? _dk.TextPrimary : _dk.TextSecondary);
        }
    }
}

/// <summary>
/// 测量规格的小工具。
/// </summary>
/// <remarks>
/// 不用 <c>View.ResolveSize</c>：那是 Java 侧的 protected static，跨语言调用得绕一层，
/// 而这里要的规则只有三条（EXACTLY 听父容器、AT_MOST 取小、UNSPECIFIED 用期望值）。
/// 写清楚比自己猜绑定形状便宜。
/// </remarks>
internal static class MeasureSpecs
{
    /// <summary>按父容器给的规格决定最终尺寸。</summary>
    internal static int Resolve(int desired, int measureSpec, int fallback)
    {
        // MeasureSpec 是 Android.Views.View 的**嵌套类**，不是顶层类型 —— 写裸名会 CS0103。
        var mode = View.MeasureSpec.GetMode(measureSpec);
        var size = View.MeasureSpec.GetSize(measureSpec);
        return mode switch
        {
            MeasureSpecMode.Exactly => size,
            MeasureSpecMode.AtMost => Math.Min(desired <= 0 ? fallback : desired, size),
            _ => desired <= 0 ? fallback : desired,
        };
    }
}

/// <summary>
/// 输入框的下沉面背景。
/// </summary>
/// <remarks>
/// Android 的 <see cref="Android.Widget.EditText"/> 默认自带一条下划线（Material 的遗留外观），
/// 那与本设计语言里的「表面色阶 + 1px 极细线」是两套东西 —— 而且它还会吃掉我们设的圆角。
/// 所以输入控件必须<b>显式</b>换掉背景，不能靠默认值。
/// </remarks>
internal readonly struct EditFieldBackground(Dk dk)
{
    /// <summary>构造背景（下沉面 + 1px 细线 + 圆角 7）。</summary>
    internal GradientDrawable Build()
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(dk.Dp(7f));
        shape.SetColor(dk.Sunken);
        shape.SetStroke(dk.Dpi(1f), dk.HairlineStrong);
        return shape;
    }
}

// ---------------- 移动端重设计新增组件 ----------------

/// <summary>源选择 Pill：36dp 高，pill 圆角，激活态带强调色软底 + 描边。</summary>
internal sealed class SourcePillView : TextView
{
    private readonly Dk _dk;
    private bool _selected;

    internal SourcePillView(Context context, Dk dk, string label)
        : base(context)
    {
        _dk = dk;
        Text = label;
        Gravity = GravityFlags.Center;
        SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        SetTypeface(dk.FontDisplay, TypefaceStyle.Normal);
        SetMinHeight(dk.Dpi(36f));
        SetPadding(dk.Dpi(16f), dk.Dpi(8f), dk.Dpi(16f), dk.Dpi(8f));
        Apply();
    }

    internal new bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            Apply();
        }
    }

    private void Apply()
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(_dk.Dp(999f));
        shape.SetColor(_selected ? _dk.AccentSoft : _dk.Sunken);
        if (_selected)
        {
            shape.SetStroke(_dk.Dpi(1f), _dk.AccentLine);
        }

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(_dk.Dp(999f));
        mask.SetColor(Color.White);
        Background = new RippleDrawable(DkButton.RippleColor(_dk.Accent, 0x2E), shape, mask);
        SetTextColor(_selected ? _dk.Accent : _dk.TextPrimary);
        SetTypeface(_dk.FontDisplay, TypefaceStyle.Normal);
    }
}

/// <summary>过滤 Chip：32dp 高，8dp 圆角，激活态绿色。</summary>
internal sealed class FilterChipView : TextView
{
    private readonly Dk _dk;
    private bool _selected;

    internal FilterChipView(Context context, Dk dk, string label)
        : base(context)
    {
        _dk = dk;
        Text = label;
        Gravity = GravityFlags.Center;
        SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        SetTypeface(_dk.FontText, TypefaceStyle.Normal);
        SetMinHeight(_dk.Dpi(32f));
        SetPadding(_dk.Dpi(12f), _dk.Dpi(6f), _dk.Dpi(12f), _dk.Dpi(6f));
        Apply();
    }

    internal new bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            Apply();
        }
    }

    private void Apply()
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(_dk.Dp(8f));
        shape.SetColor(_selected ? _dk.AccentSoft : _dk.Sunken);
        if (_selected)
        {
            shape.SetStroke(_dk.Dpi(1f), _dk.AccentLine);
        }

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(_dk.Dp(8f));
        mask.SetColor(Color.White);
        Background = new RippleDrawable(DkButton.RippleColor(_dk.Accent, 0x2E), shape, mask);
        SetTextColor(_selected ? _dk.Accent : _dk.TextSecondary);
        SetTypeface(_selected ? _dk.FontDisplay : _dk.FontText, TypefaceStyle.Normal);
    }
}

/// <summary>圆形图标按钮。默认 40dp、bg-elevated 底；可指定 44dp 或 bg-surface。</summary>
internal sealed class IconButtonView : TextView
{
    internal IconButtonView(Context context, Dk dk, string icon, float sizeDp = 40f, bool sunken = true)
        : base(context)
    {
        Text = icon;
        Gravity = GravityFlags.Center;
        TextSize = dk.TypographyFor.CardTitle;
        SetTextColor(dk.TextSecondary);
        var size = dk.Dpi(sizeDp);
        SetMinimumWidth(size);
        SetMinHeight(size);
        SetPadding(0, 0, 0, 0);

        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(dk.Dp(999f));
        shape.SetColor(sunken ? dk.Sunken : dk.CardFill);

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(dk.Dp(999f));
        mask.SetColor(Color.White);
        Background = new RippleDrawable(DkButton.RippleColor(dk.Accent, 0x2E), shape, mask);
    }

    /// <summary>重设图标与颜色。</summary>
    internal void SetIcon(string icon, Color color)
    {
        Text = icon;
        SetTextColor(color);
    }
}

/// <summary>主操作按钮：44dp 高，pill 圆角，绿色底 + 深色文字，支持前缀图标。</summary>
internal sealed class PrimaryButtonView : LinearLayout
{
    private readonly TextView _icon;
    private readonly TextView _label;

    internal PrimaryButtonView(Context context, Dk dk, string label, string? icon = null, float labelSizeSp = 14f)
        : base(context)
    {
        Orientation = Android.Widget.Orientation.Horizontal;
        SetGravity(GravityFlags.Center);
        SetMinimumHeight(dk.Dpi(44f));
        SetPadding(dk.Dpi(20f), 0, dk.Dpi(20f), 0);

        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(dk.Dp(999f));
        shape.SetColor(dk.Accent);

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(dk.Dp(999f));
        mask.SetColor(Color.White);
        Background = new RippleDrawable(DkButton.RippleColor(dk.AccentInk, 0x38), shape, mask);

        _icon = new TextView(context)
        {
            Text = icon,
            Gravity = GravityFlags.Center,
            Visibility = string.IsNullOrEmpty(icon) ? ViewStates.Gone : ViewStates.Visible,
        };
        _icon.SetTextSize(Android.Util.ComplexUnitType.Sp, labelSizeSp + 1f);
        _icon.SetTextColor(dk.AccentInk);
        AddView(_icon, new LayoutParams(dk.Dpi(18f), dk.Dpi(18f)));

        _label = new TextView(context)
        {
            Text = label,
            Gravity = GravityFlags.Center,
        };
        _label.SetTextSize(Android.Util.ComplexUnitType.Sp, labelSizeSp);
        _label.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Bold), TypefaceStyle.Normal);
        _label.SetTextColor(dk.AccentInk);
        var labelParams = new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent)
        {
            LeftMargin = string.IsNullOrEmpty(icon) ? 0 : dk.Dpi(8f),
        };
        AddView(_label, labelParams);
    }

    internal string Label
    {
        get => _label.Text ?? string.Empty;
        set => _label.Text = value;
    }
}

/// <summary>底部导航项：icon + label，激活态强调色软底。</summary>
internal sealed class BottomTabItemView : LinearLayout
{
    private readonly Dk _dk;
    private readonly TextView _icon;
    private readonly TextView _label;
    private bool _selected;

    internal BottomTabItemView(Context context, Dk dk, string icon, string label)
        : base(context)
    {
        _dk = dk;
        Orientation = Android.Widget.Orientation.Vertical;
        SetGravity(GravityFlags.Center);
        SetMinimumHeight(dk.Dpi(44f));
        SetPadding(dk.Dpi(8f), dk.Dpi(6f), dk.Dpi(8f), dk.Dpi(6f));
        Clickable = true;

        _icon = new TextView(context)
        {
            Text = icon,
            Gravity = GravityFlags.Center,
        };
        _icon.SetTextSize(Android.Util.ComplexUnitType.Sp, dk.TypographyFor.CardTitle);
        _icon.SetTextColor(dk.TextSecondary);
        AddView(_icon, new LayoutParams(dk.Dpi(20f), dk.Dpi(20f)));

        _label = new TextView(context)
        {
            Text = label,
            Gravity = GravityFlags.Center,
        };
        _label.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        _label.SetTypeface(dk.FontDisplay, TypefaceStyle.Normal);
        _label.SetTextColor(dk.TextSecondary);
        AddView(_label, new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));

        Apply();
    }

    internal new bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            Apply();
        }
    }

    private void Apply()
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(_dk.Dp(12f));
        shape.SetColor(_selected ? _dk.AccentSoft : Color.Transparent);
        if (_selected)
        {
            shape.SetStroke(_dk.Dpi(1f), _dk.AccentLine);
        }

        Background = shape;

        _icon.SetTextColor(_selected ? _dk.Accent : _dk.TextSecondary);
        _label.SetTextColor(_selected ? _dk.TextPrimary : _dk.TextSecondary);
    }
}

/// <summary>自定义开关：48×28dp 胶囊，背景用强调色 / 灰。</summary>
internal sealed class SwitchView : View
{
    private readonly Dk _dk;
    private bool _isOn;
    private readonly Paint _trackPaint = new() { AntiAlias = true };
    private readonly Paint _thumbPaint = new() { AntiAlias = true };

    internal SwitchView(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        SetWillNotDraw(false);
        Clickable = true;
        Click += (_, _) =>
        {
            IsOn = !IsOn;
            Toggled?.Invoke(this, EventArgs.Empty);
        };
        Apply();
    }

    internal bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            Apply();
            Invalidate();
        }
    }

    internal event EventHandler? Toggled;

    private void Apply()
    {
        _trackPaint.Color = _isOn ? _dk.Accent : _dk.Sunken;
        _thumbPaint.Color = _dk.TextPrimary;
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var width = _dk.Dpi(48f);
        var height = _dk.Dpi(28f);
        SetMeasuredDimension(
            MeasureSpecs.Resolve(width, widthMeasureSpec, width),
            MeasureSpecs.Resolve(height, heightMeasureSpec, height));
    }

    protected override void OnDraw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        base.OnDraw(canvas);

        var radius = Height / 2f;
        canvas.DrawRoundRect(0, 0, Width, Height, radius, radius, _trackPaint);

        var thumbRadius = Height * 0.38f;
        var thumbX = _isOn
            ? Width - radius
            : radius;
        canvas.DrawCircle(thumbX, Height / 2f, thumbRadius, _thumbPaint);
    }
}

/// <summary>
/// 设置页的「键值行」：标题在上、值在下，整体居中，整行可点。
/// </summary>
/// <remarks>
/// 设计稿把「只读/跳转型」设置项统一成这种居中两行结构（默认源、下载路径、版本…），
/// 与左侧标题 + 右侧开关的开关行区分开 —— 所以它是独立控件，而不是开关行的一个开关。
/// </remarks>
internal sealed class SettingsValueRowView : LinearLayout
{
    private readonly Dk _dk;
    private readonly TextView _title;
    private readonly TextView _value;

    internal SettingsValueRowView(Context context, Dk dk, string title, string? value)
        : base(context)
    {
        _dk = dk;
        Orientation = Android.Widget.Orientation.Vertical;
        SetGravity(GravityFlags.Center);
        SetMinimumHeight(dk.Dpi(56f));
        SetPadding(dk.Dpi(16f), dk.Dpi(8f), dk.Dpi(16f), dk.Dpi(8f));
        Clickable = true;
        Focusable = true;

        _title = new TextView(context)
        {
            Text = title,
            Gravity = GravityFlags.Center,
        };
        _title.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        _title.SetTypeface(dk.FontText, TypefaceStyle.Normal);
        _title.SetTextColor(dk.TextPrimary);
        AddView(_title, new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));

        _value = new TextView(context)
        {
            Text = value ?? string.Empty,
            Gravity = GravityFlags.Center,
        };
        _value.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        _value.SetTypeface(dk.FontText, TypefaceStyle.Normal);
        _value.SetTextColor(dk.TextSecondary);
        _value.SetSingleLine(true);
        _value.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        AddView(_value, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
    }

    internal string? ValueText
    {
        get => _value.Text;
        set => _value.Text = value ?? string.Empty;
    }
}

/// <summary>
/// 用横线拼出来的图标（设计稿的筛选 / 菜单按钮）。
/// </summary>
/// <remarks>
/// <para>系统字体里没有语义正确的「滑杆」字形（<c>≡</c> 是等长三线，会被误读成菜单），
/// 菜单按钮在设计稿里又恰恰是<b>两</b>条线 —— 字形表达不了，所以直接画。</para>
/// <para>构造时传入的 <c>widths</c> 是各条线相对基准宽度的比例，从上到下排列：
/// 滑杆是递减的 <c>1 / 0.83 / 0.61</c>，菜单是等长的 <c>1 / 1</c>。</para>
/// </remarks>
internal sealed class LinesIconView : View
{
    private readonly Dk _dk;
    private readonly Paint _paint;
    private readonly float _sizeDp;
    private readonly float[] _widths;

    internal LinesIconView(Context context, Dk dk, float[] widths, float sizeDp = 44f, bool sunken = false)
        : base(context)
    {
        _dk = dk;
        _sizeDp = sizeDp;
        _widths = widths;
        _paint = new Paint
        {
            AntiAlias = true,
            StrokeWidth = dk.Dp(1.6f),
        };
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.Color = dk.TextPrimary;
        Elevation = 0f;
        Clickable = true;
        Focusable = true;
        SetWillNotDraw(false);

        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(dk.Dp(999f));
        shape.SetColor(sunken ? dk.Sunken : dk.CardFill);

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(dk.Dp(999f));
        mask.SetColor(Color.White);
        Background = new RippleDrawable(DkButton.RippleColor(dk.Accent, 0x2E), shape, mask);
    }

    /// <summary>筛选：三线递减，44dp，bg-surface 底。</summary>
    internal static LinesIconView Sliders(Context context, Dk dk, float sizeDp = 44f) =>
        new(context, dk, [1f, 0.83f, 0.61f], sizeDp);

    /// <summary>菜单：两线等长，40dp，bg-elevated 底（设计稿右上角）。</summary>
    internal static LinesIconView Menu(Context context, Dk dk, float sizeDp = 40f) =>
        new(context, dk, [1f, 1f], sizeDp, sunken: true);

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var size = _dk.Dpi(_sizeDp);
        SetMeasuredDimension(
            MeasureSpecs.Resolve(size, widthMeasureSpec, size),
            MeasureSpecs.Resolve(size, heightMeasureSpec, size));
    }

    protected override void OnDraw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        base.OnDraw(canvas);

        if (_widths.Length == 0)
        {
            return;
        }

        var cx = Width / 2f;
        var cy = Height / 2f;
        // 线间距固定，整组线条在图标框内垂直居中。
        var gap = _dk.Dp(4.5f);
        var full = _dk.Dp(9f);
        var top = cy - (gap * (_widths.Length - 1) / 2f);
        var half = _paint.StrokeWidth / 2f;

        for (var i = 0; i < _widths.Length; i++)
        {
            var length = full * _widths[i];
            var y = Math.Clamp(top + (gap * i), half, Height - half);
            canvas.DrawLine(cx - (length / 2f), y, cx + (length / 2f), y, _paint);
        }
    }
}
