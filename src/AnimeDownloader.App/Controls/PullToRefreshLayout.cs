using Android.Content;
using Android.Views;
using Android.Widget;
using AnimeDownloader.App.Design;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 下拉刷新：把内容拉下来，顶上露出一条强调色细线。
/// </summary>
/// <remarks>
/// <para><b>为什么自己写而不是用 <c>SwipeRefreshLayout</c></b>：那个控件的视觉是一枚
/// 系统圆形箭头 —— 与本设计语言里「加载态 = 低对比的微光 / 强调色细线，绝不用旋转圈和圆箭头」
/// 直接冲突。本轮的目标之一是「触摸交互与手势支持」，而手势的视觉反馈恰恰是这套界面的一部分，
/// 不该外包给一个自带配色的系统控件。</para>
///
/// <para>行为上只做两件事，且两件事都必须正确：</para>
/// <list type="number">
///   <item><description><b>只在列表已经到顶时才能拉起</b>。列表仍在滚动中往下拖是正常的滚动意图，
///   抢过来变成刷新是最典型的手势冲突。判据是内容的 <c>CanScrollVertically(-1)</c>。</description></item>
///   <item><description><b>拖动方向必须是「纵向为主」的</b>。横向拖（画廊里左滑换页、灯箱里平移图片）
///   不能被误判成下拉刷新 —— 判据是纵向位移同时大于 touch slop 与横向位移。</description></item>
/// </list>
///
/// <para>阻尼是刻意加的：位移按 <c>拖距 / (拖距 + 视高)</c> 之类的方式收敛，越拉越沉，
/// 用户能凭手感知道「还能再拉多少」。线性跟手会让人以为可以无限拉。</para>
/// </remarks>
internal sealed class PullToRefreshLayout : FrameLayout
{
    /// <summary>触发刷新所需的下拉距离（dp）。</summary>
    private const float TriggerDistanceDp = 72f;

    /// <summary>阻尼系数：拖过触发距离后，后续位移按这个比例打折。</summary>
    private const float Resistance = 0.45f;

    private readonly Dk _dk;
    private readonly int _touchSlop;
    private readonly ThinProgressBar _indicator;
    private readonly ViewConfiguration _configuration;

    private View? _content;
    private float _downY;
    private float _downX;
    private bool _dragging;
    private bool _refreshing;
    private bool _settling;
    private float _offset;

    internal PullToRefreshLayout(Context context, Dk dk)
        : base(context)
    {
        _dk = dk;
        _configuration = ViewConfiguration.Get(context)!;
        _touchSlop = _configuration.ScaledTouchSlop;

        _indicator = new ThinProgressBar(context, dk)
        {
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, dk.Dpi(2f))
            {
                Gravity = GravityFlags.Top,
            },
            Visibility = ViewStates.Invisible,
        };
        AddView(_indicator);
    }

    /// <summary>用户松手时若已越过触发距离则触发一次。</summary>
    internal event EventHandler? RefreshRequested;

    /// <summary>是否正在刷新（刷新中不再接受新的下拉）。</summary>
    internal bool IsRefreshing => _refreshing;

    /// <summary>设置内容视图（放在指示线之下）。</summary>
    internal void SetContent(View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_content is not null)
        {
            RemoveView(_content);
        }

        _content = content;
        AddView(content, 0, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
    }

    /// <summary>结束刷新并把内容收回去。</summary>
    internal void EndRefresh()
    {
        _refreshing = false;
        _indicator.Indeterminate = false;
        AnimateOffset(0f, () =>
        {
            _indicator.Visibility = ViewStates.Invisible;
            _settling = false;
        });
    }

    public override bool OnInterceptTouchEvent(MotionEvent? e)
    {
        if (e is null || _refreshing || _settling)
        {
            return false;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downY = e.GetY();
                _downX = e.GetX();
                _dragging = false;
                return false;

            case MotionEventActions.Move:
                var deltaY = e.GetY() - _downY;
                var deltaX = Math.Abs(e.GetX() - _downX);
                // 三个条件缺一不可：向下、够远、且比横向位移更明显。
                if (deltaY > _touchSlop && deltaY > deltaX && !CanContentScrollUp())
                {
                    _dragging = true;
                    _downY = e.GetY();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return base.OnTouchEvent(e!);
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Move:
                if (!_dragging)
                {
                    return false;
                }

                _offset = Math.Max(0f, _offset + ((e.GetY() - _downY) * Resistance));
                _downY = e.GetY();
                ApplyOffset(_offset);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragging = false;
                if (_offset >= _dk.Dp(TriggerDistanceDp))
                {
                    BeginRefresh();
                }
                else
                {
                    _offset = 0f;
                    AnimateOffset(0f, () => _indicator.Visibility = ViewStates.Invisible);
                }

                return true;

            default:
                return base.OnTouchEvent(e);
        }
    }

    private void BeginRefresh()
    {
        _refreshing = true;
        _settling = true;
        // 刷新期间停在触发距离处，指示线转成不确定进度（来回扫，而不是转圈）。
        AnimateOffset(_dk.Dp(TriggerDistanceDp), () =>
        {
            _indicator.Indeterminate = true;
            _settling = false;
        });
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 内容是否还有「向上滚动」的余地（有余地就说明没到顶，此时不许下拉刷新）。
    /// </summary>
    /// <remarks>
    /// <b>不能只问 <c>_content</c> 本人。</b>外面传进来的往往是一个包装用的
    /// <see cref="ViewGroup"/>（画廊里是「网格 + 状态行」的 LinearLayout），
    /// 而非滚动的 <c>RecyclerView</c>；<c>View.CanScrollVertically</c> 的默认实现恒为 false，
    /// 于是「列表滚到一半」也会被误判成「已到顶」—— 一往下拖就变成刷新，
    /// 把用户「滚回顶部」的意图整个吃掉。所以要钻进子树找到真正在滚动的那个视图。
    /// </remarks>
    private bool CanContentScrollUp() => CanScrollUp(_content);

    private static bool CanScrollUp(View? view)
    {
        if (view is null)
        {
            return false;
        }

        if (view.CanScrollVertically(-1))
        {
            return true;
        }

        if (view is ViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                if (CanScrollUp(group.GetChildAt(i)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ApplyOffset(float offset)
    {
        if (_content is not null)
        {
            _content.TranslationY = offset;
        }

        _indicator.Visibility = offset > 0 ? ViewStates.Visible : ViewStates.Invisible;
        _indicator.TranslationY = Math.Max(0f, offset - _dk.Dpi(2f));
        var trigger = _dk.Dp(TriggerDistanceDp);
        _indicator.Progress = trigger > 0 ? Math.Clamp(offset / trigger, 0d, 1d) : 0d;
    }

    private void AnimateOffset(float target, Action? onEnd)
    {
        var from = _offset;
        _offset = target;
        if (_content is not null)
        {
            _content.Animate()!
                .TranslationY(target)
                .SetDuration(180)
                .WithEndAction(new Java.Lang.Runnable(() =>
                {
                    ApplyOffset(target);
                    onEnd?.Invoke();
                }))
                .Start();
        }
        else
        {
            ApplyOffset(target);
            onEnd?.Invoke();
        }
    }
}
