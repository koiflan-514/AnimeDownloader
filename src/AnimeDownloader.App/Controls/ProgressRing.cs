using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 进度环。Avalonia 没有 <c>ProgressRing</c>，而原版有三处在用（画廊页脚 15px、
/// 灯箱 40px、独立大图窗口 36px、设置页网络检测 18px），全部是「正在进行」的语义色
/// —— 也就是强调色。所以这里自绘：一段 100° 的圆弧绕圈，圆头端点。
///
/// 尺寸与「不活动时仍占位」的语义都保留：WinUI 的 ProgressRing 在 IsActive=false 时
/// 不可见但依然占据 15×15 的布局空间，页脚轨道的横向排布因此不会跳动。
/// 这里用「Render 提前返回」实现同一效果 —— 不画，但参与布局。
/// </summary>
public sealed class ProgressRing : Control
{
    /// <summary>是否正在转动。false 时不绘制任何内容。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsActive));

    /// <summary>环的颜色。默认由主题设成强调色。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));

    /// <summary>线宽。</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(StrokeThickness), 2.5);

    /// <summary>每帧推进的相位（一圈 = 1.0）。约 1.6 秒一圈。</summary>
    private const double PhasePerFrame = 16.0 / 1600.0;

    /// <summary>圆弧张角（度）。</summary>
    private const double SweepDegrees = 100;

    private DispatcherTimer? _timer;
    private double _phase;

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(ForegroundProperty, StrokeThicknessProperty);
    }

    public ProgressRing()
    {
        // 用事件而不是重写 OnDetachedFromVisualTree：后者的参数类型
        // VisualTreeAttachmentEventArgs 是 internal 的，进不了方法签名。
        // 离开视觉树就停表 —— 画廊缓存了三张页面，不主动停会让所有页缓存的环一直转。
        DetachedFromVisualTree += (_, _) => _timer?.Stop();
    }

    /// <summary>是否正在转动。</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>环的颜色。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>线宽。</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            UpdateTimer();
        }
    }

    /// <summary>
    /// 只在真正活动时挂一个 60fps 的计时器。
    /// 不用 <c>Animation</c> 的自旋：那个方案需要一个额外的哑元属性承载关键帧，
    /// 而这个环的形状是「相位驱动的绘制」，用计时器 + InvalidateVisual 更直白。
    /// </summary>
    private void UpdateTimer()
    {
        if (IsActive)
        {
            _timer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render,
                (_, _) =>
                {
                    _phase = (_phase + PhasePerFrame) % 1.0;
                    InvalidateVisual();
                });
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive || Foreground is not { } brush)
        {
            return;
        }

        var size = Bounds.Size;
        var thickness = StrokeThickness;
        var radius = (Math.Min(size.Width, size.Height) - thickness) / 2;
        if (radius <= 0.5)
        {
            return;
        }

        var center = new Point(size.Width / 2, size.Height / 2);
        DrawArc(context, center, radius, _phase * 360.0, brush, thickness);
    }

    private static void DrawArc(
        DrawingContext context,
        Point center,
        double radius,
        double startDegrees,
        IBrush brush,
        double thickness)
    {
        var start = startDegrees * Math.PI / 180.0;
        var end = (startDegrees + SweepDegrees) * Math.PI / 180.0;
        var from = new Point(center.X + (radius * Math.Cos(start)), center.Y + (radius * Math.Sin(start)));
        var to = new Point(center.X + (radius * Math.Cos(end)), center.Y + (radius * Math.Sin(end)));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false);
            // 6 个位置参数：终点 / 半径 / 旋转角 / 大弧标记 / 方向 / 是否描边
            ctx.ArcTo(to, new Size(radius, radius), 0, SweepDegrees > 180, SweepDirection.Clockwise, true);
            ctx.EndFigure(false);
        }

        var pen = new Pen(brush, thickness, null, PenLineCap.Round);
        context.DrawGeometry(null, pen, geometry);
    }
}
