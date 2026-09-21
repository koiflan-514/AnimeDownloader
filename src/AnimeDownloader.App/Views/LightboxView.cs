using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.Runtime;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Android.Animation;
using AnimeDownloader.App.Controls;
using AnimeDownloader.App.Design;
using AnimeDownloader.App.Platform;
using AnimeDownloader.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 灯箱：单张大图查看。参照暗房设计规范 §5.2「灯箱 = Light Table」。
/// </summary>
/// <remarks>
/// <para>版式上尽量让出竖直空间给图片：头部只有眉标加一行等宽元数据，底部只有一条状态轨，
/// 图片嵌在一块深色凹槽里。控件全部退化为浮在暗面上的玻璃，符合「图片是唯一光源」的隐喻。</para>
///
/// <para>手势是这里的重点，且全部用 Android 原生手势检测器实现，避免自己算指针：
/// 双指捏合用 <see cref="ScaleGestureDetector"/>，双击 / 滑动 / 惯性用 <see cref="GestureDetector"/>。
/// 两者都喂同一份 <see cref="MotionEvent"/>，由本类统一裁决——放大态下拖动平移、未放大态下
/// 竖直下滑关闭或横向切换。所有触摸目标都取 <c>dk.TouchTarget</c>（48dp）以满足无障碍下限。</para>
///
/// <para>内存安全：切换图片必须取消上一张的加载（<see cref="CancellationTokenSource"/>），
/// 否则手机上快速翻页会同时解码一堆大图——这是最典型的 OOM 来源。加载走
/// <see cref="AnimeDownloader.App.Thumbnails.ThumbnailLoader"/> 的采样解码 + 位图 LRU，
/// 对原图 URL 也适用，是内存安全的那条路。</para>
/// </remarks>
internal sealed class LightboxView : FrameLayout,
    View.IOnTouchListener,
    ScaleGestureDetector.IOnScaleGestureListener,
    GestureDetector.IOnGestureListener,
    GestureDetector.IOnDoubleTapListener
{
    /// <summary>缩放范围下限：适应屏幕（1×）。</summary>
    private const float MinScale = 1f;

    /// <summary>缩放范围上限（以「适应屏幕」为 1× 的相对倍率）。</summary>
    private const float MaxScale = 8f;

    /// <summary>判断「是否处于适应屏幕」的容差，避开浮点抖动。</summary>
    private const float Epsilon = 0.001f;

    /// <summary>未放大时判定滑动方向的死区（px）。</summary>
    private const float DirectionDeadZone = 12f;

    /// <summary>未放大时横滑切换上一张/下一张的位移阈值（px）。</summary>
    private const float SwitchThresholdDp = 60f;

    private readonly Context _context;
    private readonly Dk _dk;
    private readonly AppServices _services;
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly GestureDetector _gestureDetector;

    private IReadOnlyList<ImageItem>? _items;
    private int _index;

    // 以下字段在 BuildChrome（构造期内调用）中赋值，故不能为 readonly；
    // 用 null! 抑制「未明确赋值」检查，因为确定赋值分析不追踪被调用方法内部的赋值。
    private FrameLayout _stage = null!;
    private ImageView _imageView = null!;
    private ThinProgressBar _progress = null!;
    private EmptyStateView _errorPanel = null!;
    private View _topOverlay = null!;
    private View _bottomOverlay = null!;
    private TextView _title = null!;
    private TextView _author = null!;
    private TextView _meta = null!;
    private LinearLayout _tagsRow = null!;
    private PrimaryButtonView _saveButton = null!;

    private Bitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private float _baseScale;
    private float _userScale = 1f;
    private float _transX;
    private float _transY;

    private CancellationTokenSource? _cts;

    private DragMode _dragMode = DragMode.None;
    private float _dragDx;
    private float _dragDy;

    private bool _isFullscreen;
    private bool _hasTags;

    private ValueAnimator? _flingAnimator;
    private ValueAnimator? _sheetAnimator;

    private Func<ImageItem, Task<string>>? _saveHandler;

    /// <summary>宿主需要把灯箱从视图树摘掉时触发（下滑关闭 / 关闭按钮 / 系统返回键）。</summary>
    internal event EventHandler? CloseRequested;

    /// <summary>点击一个标签时触发，让宿主把它填进画廊的标签搜索（灯箱不直接做搜索）。</summary>
    internal event EventHandler<string>? TagSelected;

    /// <summary>
    /// 把当前这张保存到设备。宿主负责接到下载模块上（灯箱不直接依赖模块的实例）。
    /// </summary>
    /// <remarks>
    /// 之所以用函数而不是让灯箱自己建下载模块：下载模块的懒加载入口
    /// <c>AppServices.CreateDownloadModule</c> 需要宿主 Activity 类型，且模块实例由宿主独占维护。
    /// 灯箱只关心「给我一个落点字符串，或在异常里告诉我失败原因」，保存的具体实现交给宿主。
    /// </remarks>
    internal Func<ImageItem, Task<string>>? SaveHandler
    {
        get => _saveHandler;
        set
        {
            _saveHandler = value;
            if (_saveButton is not null)
            {
                _saveButton.Enabled = value is not null;
            }
        }
    }

    internal LightboxView(Context context, Dk dk, AppServices services)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dk);
        ArgumentNullException.ThrowIfNull(services);

        _context = context;
        _dk = dk;
        _services = services;

        // 灯箱整片是「房间」底色；亮色模式下舞台仍是深的（Dk 已单独处理），二者形成对比。
        SetBackgroundColor(dk.Background);
        Elevation = 0f;

        _scaleDetector = new ScaleGestureDetector(context, this);
        _gestureDetector = new GestureDetector(context, this);
        // 双击切 1×/2× 必须显式挂上双击监听器，否则 GestureDetector 只认单击/长按/滑动。
        _gestureDetector.SetOnDoubleTapListener(this);

        BuildChrome();

        // 保存按钮初始是否可用取决于宿主是否接了 SaveHandler。
        _saveButton.Enabled = _saveHandler is not null;
    }

    /// <summary>构建并铺设整页结构。所有子视图只建一次，后续由 Show 填充动态内容。</summary>
    private void BuildChrome()
    {
        var match = ViewGroup.LayoutParams.MatchParent;
        var wrap = ViewGroup.LayoutParams.WrapContent;

        _chrome = new FrameLayout(_context);

        // ---- 舞台：图片铺满整屏，底色 bg-surface（设计稿的 Viewer Stage）。----
        _stage = new FrameLayout(_context);
        _stage.SetBackgroundColor(_dk.CardFill);

        _imageView = new ImageView(_context)
        {
            LayoutParameters = new FrameLayout.LayoutParams(match, match),
        };
        // 缩放/平移完全由我们手动算矩阵驱动，所以缩放模式必须是 Matrix。
        _imageView.SetScaleType(ImageView.ScaleType.Matrix);

        _progress = new ThinProgressBar(_context, _dk)
        {
            Indeterminate = true,
            Visibility = ViewStates.Gone,
            LayoutParameters = new FrameLayout.LayoutParams(_dk.Dpi(160f), wrap)
            {
                Gravity = GravityFlags.Center,
            },
        };

        _errorPanel = new EmptyStateView(_context, _dk)
        {
            LayoutParameters = new FrameLayout.LayoutParams(wrap, wrap)
            {
                Gravity = GravityFlags.Center,
            },
        };
        _errorPanel.RetryRequested += (_, _) => LoadCurrent();

        _stage.AddView(_imageView);
        _stage.AddView(_progress);
        _stage.AddView(_errorPanel);
        _chrome.AddView(_stage, new FrameLayout.LayoutParams(match, match));

        // ---- 顶部渐变遮罩：黑 70% → 透明，承载返回 / 标题 / 更多。----
        _topOverlay = BuildTopOverlay();
        _chrome.AddView(_topOverlay, new FrameLayout.LayoutParams(match, _dk.Dpi(120f))
        {
            Gravity = GravityFlags.Top,
        });

        // ---- 底部渐变遮罩：透明 → 黑 80%，承载作者 / 元数据 / 标签 / 操作。----
        _bottomOverlay = BuildBottomOverlay();
        _chrome.AddView(_bottomOverlay, new FrameLayout.LayoutParams(match, wrap)
        {
            Gravity = GravityFlags.Bottom,
        });

        AddView(_chrome, match, match);

        // 手势只在舞台上裁决；上下遮罩里的按钮走各自的点击。
        _stage.SetOnTouchListener(this);
    }

    /// <summary>顶部渐变条：返回 + 当前源/模式 + 更多。</summary>
    private LinearLayout BuildTopOverlay()
    {
        var bar = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(_dk.Dpi(16f), _dk.Dpi(16f), _dk.Dpi(16f), 0);
        bar.Background = BuildGradient(
            Color.Argb(0xB3, 0x00, 0x00, 0x00),
            Color.Transparent);

        var back = new IconButtonView(_context, _dk, DkIcons.Back);
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        bar.AddView(back, new LinearLayout.LayoutParams(_dk.Dpi(40f), _dk.Dpi(40f)));

        _title = new TextView(_context)
        {
            Gravity = GravityFlags.Center,
        };
        _title.SetSingleLine(true);
        _title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _title.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        _title.SetTypeface(_dk.FontDisplay, TypefaceStyle.Normal);
        _title.SetTextColor(_dk.TextPrimary);
        bar.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            LeftMargin = _dk.Dpi(12f),
            RightMargin = _dk.Dpi(12f),
        });

        var more = new IconButtonView(_context, _dk, DkIcons.MoreVertical);
        more.Click += (_, _) => OpenSource();
        bar.AddView(more, new LinearLayout.LayoutParams(_dk.Dpi(40f), _dk.Dpi(40f)));

        return bar;
    }

    /// <summary>底部渐变条：作者 / 尺寸 / 标签 + 下载 · 分享 · 收藏。</summary>
    private LinearLayout BuildBottomOverlay()
    {
        var column = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        column.SetGravity(GravityFlags.Bottom);
        // 上留 40dp 纯渐变余量：文字落在遮罩已经足够暗的那一段，
        // 否则遇到白底大图时作者名会糊在浅色上（设计稿的底图是深灰，看不出这个问题）。
        column.SetPadding(_dk.Dpi(16f), _dk.Dpi(40f), _dk.Dpi(16f), _dk.Dpi(32f));
        column.Background = BuildGradient(
            Color.Transparent,
            Color.Argb(0x90, 0x00, 0x00, 0x00),
            Color.Argb(0xE0, 0x00, 0x00, 0x00));

        var info = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        column.AddView(info, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        _author = new TextView(_context)
        {
            Text = "—",
        };
        _author.SetSingleLine(true);
        _author.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _author.SetTextSize(Android.Util.ComplexUnitType.Sp, 18f);
        _author.SetTypeface(_dk.FontDisplay, TypefaceStyle.Bold);
        _author.SetTextColor(_dk.TextPrimary);
        AddScrim(_author);
        info.AddView(_author);

        _meta = new TextView(_context);
        _meta.SetSingleLine(true);
        _meta.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _meta.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        _meta.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
        _meta.SetTextColor(_dk.TextSecondary);
        AddScrim(_meta);
        info.AddView(_meta, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = _dk.Dpi(4f),
        });

        _tagsRow = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        info.AddView(_tagsRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = _dk.Dpi(4f),
        });

        // 操作条：绿色主按钮 + 分享 + 收藏（后两者各占一半剩余宽度）。
        var actions = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.CenterVertical);
        column.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = _dk.Dpi(12f),
        });

        _saveButton = new PrimaryButtonView(_context, _dk, "下载", DkIcons.Download, 15f);
        _saveButton.Click += OnSave;
        actions.AddView(_saveButton);

        var share = BuildOverlayButton(DkIcons.Share);
        share.Click += (_, _) => ShareCurrent();
        actions.AddView(share, new LinearLayout.LayoutParams(0, _dk.Dpi(44f), 1f)
        {
            LeftMargin = _dk.Dpi(12f),
        });

        var favorite = BuildOverlayButton(DkIcons.Heart);
        favorite.Click += (_, _) => SetIdle("已标记收藏");
        actions.AddView(favorite, new LinearLayout.LayoutParams(0, _dk.Dpi(44f), 1f)
        {
            LeftMargin = _dk.Dpi(12f),
        });

        return column;
    }

    /// <summary>遮罩上的次要图标按钮：bg-elevated 药丸、图标居中。</summary>
    private TextView BuildOverlayButton(string icon)
    {
        var button = new TextView(_context)
        {
            Text = icon,
            Gravity = GravityFlags.Center,
        };
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 16f);
        button.SetTextColor(_dk.TextPrimary);

        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetCornerRadius(_dk.Dp(999f));
        shape.SetColor(_dk.Sunken);

        var mask = new GradientDrawable();
        mask.SetShape(ShapeType.Rectangle);
        mask.SetCornerRadius(_dk.Dp(999f));
        mask.SetColor(Color.White);
        button.Background = new RippleDrawable(DkButton.RippleColor(_dk.Accent, 0x2E), shape, mask);
        return button;
    }

    /// <summary>竖直渐变背景（上 → 下），支持三档色标让中段更快压暗。</summary>
    private static GradientDrawable BuildGradient(params Color[] colors)
    {
        // 绑定层的构造函数收 int[]（ARGB），不是 Color[]。
        var argb = new int[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            argb[i] = colors[i].ToArgb();
        }

        var drawable = new GradientDrawable(GradientDrawable.Orientation.TopBottom, argb);
        drawable.SetShape(ShapeType.Rectangle);
        return drawable;
    }

    /// <summary>
    /// 给遮罩上的文字加一层投影。
    /// </summary>
    /// <remarks>
    /// 大图的底色不可控（白底插画很常见），纯靠渐变保证对比度不够稳；
    /// 一层深色投影能兜住最坏情况，而且在深底图上几乎看不出来。
    /// </remarks>
    private static void AddScrim(TextView view) =>
        view.SetShadowLayer(6f, 0f, 1f, Color.Argb(0xD9, 0x00, 0x00, 0x00));

    /// <summary>显示第 index 张（items 为整批，可左右切换）。index 越界返回 false。</summary>
    internal bool Show(IReadOnlyList<ImageItem> items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (index < 0 || index >= items.Count)
        {
            return false;
        }

        _items = items;
        GoToInternal(index);
        return true;
    }

    /// <summary>切换到指定下标并刷新头部、标签与图片（带边界夹取）。</summary>
    private void GoToInternal(int index)
    {
        if (_items is null)
        {
            return;
        }

        _index = Math.Clamp(index, 0, _items.Count - 1);
        UpdateHeader();
        PopulateTags();
        LoadCurrent();
    }

    /// <summary>处理系统返回键。返回 true 表示已消费（先退全屏、再退缩放、最后关闭）。</summary>
    internal bool HandleBackPressed()
    {
        if (_isFullscreen)
        {
            SetFullscreen(false);
            return true;
        }

        if (_userScale > MinScale + Epsilon)
        {
            ZoomToCentered(MinScale);
            return true;
        }

        return false;
    }

    /// <summary>刷新眉标元数据行与「打开来源」按钮的可见性。</summary>
    private void UpdateHeader()
    {
        if (_items is null)
        {
            return;
        }

        var item = _items[_index];

        // 顶部标题：当前源 · 模式（设计稿的 Viewer Title）。
        var source = _services.CurrentSource;
        var modeText = _services.Settings.GallerySubmode == "paged" ? "分页模式" : "随机模式";
        _title.Text = $"{source.DisplayName} · {modeText}";

        // 底部信息：作者 / 尺寸 · 格式 / 标签。
        _author.Text = string.IsNullOrWhiteSpace(item.Artist) ? "未知作者" : item.Artist;

        var meta = item.Dimensions is { } dim ? $"{dim.Width} × {dim.Height}" : string.Empty;
        if (!string.IsNullOrEmpty(item.Extension))
        {
            meta = meta.Length > 0 ? $"{meta} · {item.Extension}" : item.Extension;
        }

        _meta.Text = meta;
    }

    /// <summary>填充当前图片的全部标签 chip；点击一个即请求宿主按该标签搜索。</summary>
    private void PopulateTags()
    {
        _tagsRow.RemoveAllViews();
        _hasTags = false;

        if (_items is null)
        {
            _tagsRow.Visibility = ViewStates.Gone;
            return;
        }

        var item = _items[_index];
        var tags = item.TagList;
        _hasTags = tags.Count > 0;
        _tagsRow.Visibility = _hasTags ? ViewStates.Visible : ViewStates.Gone;
        if (!_hasTags)
        {
            return;
        }

        // 设计稿里标签是一行纯文字（#tag #tag），不是描边 chip —— 只保留文字与可点性。
        // 最多 6 个：再多会溢出到遮罩外，反而读不全。
        var count = Math.Min(tags.Count, 6);
        for (var i = 0; i < count; i++)
        {
            var tagName = tags[i].Name;
            var chip = new TextView(_context)
            {
                Text = $"#{tagName}",
                Ellipsize = Android.Text.TextUtils.TruncateAt.End,
            };
            chip.SetSingleLine(true);
            chip.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
            chip.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
            chip.SetTextColor(_dk.TextTertiary);
            AddScrim(chip);
            chip.Click += (_, _) => TagSelected?.Invoke(this, tagName);
            _tagsRow.AddView(chip, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                RightMargin = _dk.Dpi(6f),
            });
        }
    }

    /// <summary>加载当前这张图片。会取消上一张仍在进行的加载，避免并发解码大图。</summary>
    private async void LoadCurrent()
    {
        if (_items is null)
        {
            return;
        }

        var item = _items[_index];

        // 取消上一张：翻页/重试都必须让旧请求尽快让路，否则手机内存会被一堆大图解码吃爆。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        SetBusy("加载中…");
        _progress.Visibility = ViewStates.Visible;
        _errorPanel.Hide();

        Bitmap? bmp = null;
        try
        {
            var target = ResolveTargetPx();
            bmp = await _services.Thumbnails.LoadAsync(item.Url, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            bmp = null;
        }

        // 取消优先：被新一次加载取代的旧结果直接丢弃，不污染界面。
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // 回到 UI 线程更新位图：Android 不允许在非 UI 线程触碰视图。
        var captured = bmp;
        this.Post(() => OnBitmapReady(item, captured, cts));
    }

    /// <summary>解码完成后的 UI 落地（已在 UI 线程）。</summary>
    private void OnBitmapReady(ImageItem item, Bitmap? bmp, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested || _items is null || _items[_index] != item)
        {
            return;
        }

        _progress.Visibility = ViewStates.Gone;

        if (bmp is null)
        {
            ShowError("加载失败：图片为空或解码失败");
            return;
        }

        _bitmap = bmp;
        _bitmapW = bmp.Width;
        _bitmapH = bmp.Height;
        _imageView.SetImageBitmap(bmp);
        FitImage();
        SetIdle("就绪");
    }


    /// <summary>计算采样解码的目标边长：留一点缩放余量（2× 画布最长边，封顶 4096），又不至于 OOM。</summary>
    private int ResolveTargetPx()
    {
        var w = _stage.Width;
        var h = _stage.Height;
        int longest;
        if (w > 0 && h > 0)
        {
            longest = Math.Max(w, h);
        }
        else
        {
            // 尚未布局时退回屏幕最长边，仍给 2× 余量。
            var dm = _context.Resources?.DisplayMetrics;
            longest = dm is null
                ? 1080
                : Math.Max(dm.WidthPixels, dm.HeightPixels);
        }

        return (int)Math.Min(2L * longest, 4096);
    }

    /// <summary>以「适应屏幕」为基准重置变换并居中（缩放倍率 1×）。</summary>
    private void FitImage()
    {
        var vw = _imageView.Width;
        var vh = _imageView.Height;
        if (vw <= 0 || vh <= 0 || _bitmapW <= 0 || _bitmapH <= 0)
        {
            return;
        }

        _baseScale = Math.Min((float)vw / _bitmapW, (float)vh / _bitmapH);
        _userScale = MinScale;
        _transX = (vw - _bitmapW * _baseScale) / 2f;
        _transY = (vh - _bitmapH * _baseScale) / 2f;
        ClampAndApply();
    }

    /// <summary>以图片中心为焦点缩放（工具栏按钮用）。</summary>
    private void ZoomToCentered(float newUser)
    {
        ZoomTo(newUser, _imageView.Width / 2f, _imageView.Height / 2f);
    }

    /// <summary>缩放到实际像素大小（1:1），焦点取图片中心。</summary>
    private void ZoomToActualSize()
    {
        if (_baseScale <= 0)
        {
            return;
        }

        ZoomToCentered(1f / _baseScale);
    }

    /// <summary>以 (fx, fy) 为焦点缩放到 newUser 倍，焦点点保持不动（用于捏合与双击）。</summary>
    private void ZoomTo(float newUser, float fx, float fy)
    {
        if (_bitmap is null || _baseScale <= 0)
        {
            return;
        }

        newUser = Math.Clamp(newUser, MinScale, MaxScale);
        // ratio = 新总缩放 / 旧总缩放；焦点点在新旧矩阵下必须映射到同一屏幕坐标。
        var ratio = (_baseScale * newUser) / (_baseScale * _userScale);
        _transX = fx - ratio * (fx - _transX);
        _transY = fy - ratio * (fy - _transY);
        _userScale = newUser;
        ClampAndApply();
    }

    /// <summary>夹住平移边界：放大时不能把图拖出画面留白；未放大时锁定居中（仅留回弹余地即正好居中）。</summary>
    private void ClampAndApply()
    {
        if (_bitmap is null)
        {
            return;
        }

        var vw = _imageView.Width;
        var vh = _imageView.Height;
        if (vw <= 0 || vh <= 0 || _bitmapW <= 0 || _bitmapH <= 0)
        {
            return;
        }

        var display = _baseScale * _userScale;
        var scaledW = _bitmapW * display;
        var scaledH = _bitmapH * display;

        float minX, maxX;
        if (scaledW <= vw)
        {
            // 图比视口窄：只能居中，不允许平移制造留白。
            minX = maxX = (vw - scaledW) / 2f;
        }
        else
        {
            minX = vw - scaledW;
            maxX = 0f;
        }

        float minY, maxY;
        if (scaledH <= vh)
        {
            minY = maxY = (vh - scaledH) / 2f;
        }
        else
        {
            minY = vh - scaledH;
            maxY = 0f;
        }

        _transX = Math.Clamp(_transX, minX, maxX);
        _transY = Math.Clamp(_transY, minY, maxY);

        var matrix = new Matrix();
        matrix.SetScale(display, display);
        matrix.PostTranslate(_transX, _transY);
        _imageView.ImageMatrix = matrix;
    }

    /// <summary>全屏切换：隐藏系统栏，并把页内控件一起收起，图片独占整屏。</summary>
    private void SetFullscreen(bool full)
    {
        if (full == _isFullscreen)
        {
            return;
        }

        _isFullscreen = full;

        // 图片本来就铺满整屏，全屏只是把上下两条渐变遮罩收掉。
        _topOverlay.Visibility = full ? ViewStates.Gone : ViewStates.Visible;
        _bottomOverlay.Visibility = full ? ViewStates.Gone : ViewStates.Visible;

        // 拿不到 Activity（例如上下文不是 Activity）就跳过系统栏收起这一步，不影响页内收起。
        if (_context is Activity activity)
        {
            EdgeToEdge.SetBarsHidden(activity, full);
        }

        // 布局变化后重新贴合新视口（仅当处于适应屏幕时，放大态保留用户的缩放）。
        if (_bitmap is not null && _userScale <= MinScale + Epsilon)
        {
            FitImage();
        }
    }

    /// <summary>分享当前图片的源地址。</summary>
    private void ShareCurrent()
    {
        if (_items is null)
        {
            return;
        }

        var url = _items[_index].Url;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(Intent.ExtraText, url);
            var chooser = Intent.CreateChooser(intent, "分享图片地址");
            if (chooser is not null)
            {
                _context.StartActivity(chooser);
            }
        }
        catch (Exception)
        {
            SetError("无法分享");
        }
    }

    /// <summary>打开当前图片的来源链接（宿主没给来源时按钮已隐藏）。</summary>
    private void OpenSource()
    {
        if (_items is null)
        {
            return;
        }

        var link = _items[_index].SourceLink;
        if (string.IsNullOrEmpty(link))
        {
            return;
        }

        try
        {
            // Uri 必须全限定：Android.Net.Uri 与 System.Uri 同名，裸写 Uri 会 CS0104。
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(link));
            _context.StartActivity(intent);
        }
        catch (Exception)
        {
            SetError("无法打开来源链接");
        }
    }

    /// <summary>保存当前这一张：交给宿主接好的 SaveHandler，拿回落点或捕获异常进状态轨。</summary>
    private async void OnSave(object? sender, EventArgs e)
    {
        if (_saveHandler is null || _items is null)
        {
            return;
        }

        var handler = _saveHandler;
        var item = _items[_index];
        _saveButton.Enabled = false;
        SetBusy("保存中…");

        try
        {
            var location = await handler(item).ConfigureAwait(false);
            var captured = location;
            this.Post(() => SetIdle($"已保存：{captured}"));
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            this.Post(() => SetError($"保存失败：{message}"));
        }
        finally
        {
            this.Post(() =>
            {
                if (_saveButton is not null)
                {
                    _saveButton.Enabled = _saveHandler is not null;
                }
            });
        }
    }

    /// <summary>
    /// 状态反馈。
    /// </summary>
    /// <remarks>
    /// 设计稿里灯箱没有状态轨 —— 图片本身是唯一焦点。所以短暂反馈走系统 Toast（保存结果、
    /// 收藏提示），失败则在画布中央显示重试面板；「就绪」这类无信息量的话不再打扰用户。
    /// </remarks>
    private void SetIdle(string text)
    {
        if (!string.Equals(text, "就绪", StringComparison.Ordinal))
        {
            ShowToast(text);
        }
    }

    private static void SetBusy(string text)
    {
        // 进度条本身就是忙碌指示，不再额外提示。
        _ = text;
    }

    private void SetError(string text) => ShowToast(text);

    private void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Android.Widget.Toast.MakeText(_context, text, Android.Widget.ToastLength.Short)?.Show();
    }

    /// <summary>加载失败时画布中央显示等宽失败说明 + 重试按钮，不让异常冒出去。</summary>
    private void ShowError(string message)
    {
        _progress.Visibility = ViewStates.Gone;
        _errorPanel.Show("加载失败", message, "重试");
        SetError(message);
    }

    /// <summary>布局变化时（首次显示、旋转、全屏切换）重新贴合图片到新视口。</summary>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        base.OnLayout(changed, l, t, r, b);
        // 只在适应屏幕态自动重贴合；放大态保留用户的缩放，不被布局事件reset。
        if (changed && _bitmap is not null && _userScale <= MinScale + Epsilon)
        {
            FitImage();
        }
    }

    // ---------------- 触摸：把手势裁决集中到一处 ----------------

    /// <summary>
    /// 触摸入口。把同一份事件同时喂给捏合检测器和手势检测器，并在抬起时收尾未放大态的
    /// 下滑关闭 / 横滑切换。落在浮动工具栏上的触摸直接放行，交给按钮自己处理。
    /// </summary>
    public bool OnTouch(View? view, MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        if (e.ActionMasked == MotionEventActions.Down)
        {
            // 新一次触摸：清掉残留的惯性/回弹动画与下滑位移，避免状态串味。
            _flingAnimator?.Cancel();
            _sheetAnimator?.Cancel();
            TranslationY = 0f;
            Alpha = 1f;
            _dragMode = DragMode.None;
            _dragDx = 0f;
            _dragDy = 0f;
        }

        _scaleDetector.OnTouchEvent(e);
        _gestureDetector.OnTouchEvent(e);

        if (e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
        {
            // 捏合进行中不动用滑动关闭，否则会和缩放抢事件。
            if (!_scaleDetector.IsInProgress)
            {
                if (_dragMode == DragMode.VerticalClose)
                {
                    FinalizeCloseDrag();
                }
                else if (_dragMode == DragMode.HorizontalSwitch)
                {
                    FinalizeSwitchDrag();
                }
            }

            _dragMode = DragMode.None;
            _dragDx = 0f;
            _dragDy = 0f;
        }

        // 台面上的手势归灯箱自己处理，吞掉以免父容器（画廊）误滚动。
        return true;
    }

    /// <summary>抬起时根据下滑位移决定关闭还是回弹归位。</summary>
    private void FinalizeCloseDrag()
    {
        var dy = TranslationY;
        var threshold = Math.Max(_dk.Dp(120f), Height / 4f);
        if (dy >= threshold)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            AnimateSheetHome();
        }
    }

    /// <summary>抬起时根据横滑位移决定切上一张还是下一张。</summary>
    private void FinalizeSwitchDrag()
    {
        var threshold = _dk.Dp(SwitchThresholdDp);
        if (Math.Abs(_dragDx) > threshold && Math.Abs(_dragDx) > Math.Abs(_dragDy))
        {
            // 向左滑（dx<0）表示下一张，向右滑表示上一张——与画廊方向一致。
            GoToInternal(_dragDx < 0 ? _index + 1 : _index - 1);
        }
    }

    /// <summary>把整片灯箱（下滑未达阈值时）动画回弹到原位并恢复不透明度。</summary>
    private void AnimateSheetHome()
    {
        _sheetAnimator?.Cancel();
        var anim = ValueAnimator.OfFloat(0f, 1f)!;
        anim.SetDuration(220);
        anim.SetInterpolator(new DecelerateInterpolator());
        var fromTy = TranslationY;
        var fromA = Alpha;
        _sheetAnimator = anim;
        anim.AddUpdateListener(new SheetUpdateListener(this, fromTy, 0f, fromA, 1f));
        anim.Start();
    }

    /// <summary>双指捏合：以捏合焦点为中心缩放，倍率夹在 1×–8×。</summary>
    public bool OnScale(ScaleGestureDetector? detector)
    {
        if (detector is null || _bitmap is null)
        {
            return true;
        }

        var newUser = Math.Clamp(_userScale * detector.ScaleFactor, MinScale, MaxScale);
        if (Math.Abs(newUser - _userScale) < Epsilon)
        {
            return true;
        }

        ZoomTo(newUser, detector.FocusX, detector.FocusY);
        return true;
    }

    /// <summary>开始捏合：返回 true 才进入缩放流程。</summary>
    public bool OnScaleBegin(ScaleGestureDetector? detector) => true;

    /// <summary>捏合结束：无需特殊处理，矩阵已在每次 OnScale 落好。</summary>
    public void OnScaleEnd(ScaleGestureDetector? detector)
    {
    }

    /// <summary>手势开始必须返回 true，后续 move/up 才会继续送达。</summary>
    public bool OnDown(MotionEvent? e) => true;

    public void OnShowPress(MotionEvent? e)
    {
    }

    public bool OnSingleTapUp(MotionEvent? e) => false;

    /// <summary>
    /// 单指拖动：放大态下做平移（夹边界）；未放大态下据首次显著位移方向决定
    /// 「竖直下滑关闭」或「横向切换」，其余交给 Finalize* 在抬起时收尾。
    /// </summary>
    public bool OnScroll(MotionEvent? e1, MotionEvent? e2, float distanceX, float distanceY)
    {
        if (e1 is null || e2 is null || _bitmap is null)
        {
            return false;
        }

        if (_userScale > MinScale + Epsilon)
        {
            // distanceX = 上一帧 - 当前帧，所以反向加回即等于手指位移。
            _transX -= distanceX;
            _transY -= distanceY;
            ClampAndApply();
            return true;
        }

        var dxRaw = e2.RawX - e1.RawX;
        var dyRaw = e2.RawY - e1.RawY;
        _dragDx = dxRaw;
        _dragDy = dyRaw;
        if (_dragMode == DragMode.None)
        {
            if (Math.Abs(dxRaw) > DirectionDeadZone || Math.Abs(dyRaw) > DirectionDeadZone)
            {
                _dragMode = Math.Abs(dxRaw) > Math.Abs(dyRaw)
                    ? DragMode.HorizontalSwitch
                    : DragMode.VerticalClose;
            }
        }

        if (_dragMode == DragMode.VerticalClose)
        {
            // 只允许向下拖：向上回弹归零。整片下移并随位移淡出，作为「松手即关」的反馈。
            var dy = Math.Max(0f, dyRaw);
            TranslationY = dy;
            Alpha = Math.Clamp(1f - dy / (Height * 0.6f), 0.25f, 1f);
        }
        else if (_dragMode == DragMode.HorizontalSwitch)
        {
            _dragDx = dxRaw;
        }

        return true;
    }

    public void OnLongPress(MotionEvent? e)
    {
    }

    /// <summary>惯性滑动：仅在放大态用于平移；未放大态交给横滑切换逻辑，不在此飞动。</summary>
    public bool OnFling(MotionEvent? e1, MotionEvent? e2, float velocityX, float velocityY)
    {
        if (_userScale <= MinScale + Epsilon)
        {
            return false;
        }

        StartFling(velocityX, velocityY);
        return true;
    }

    /// <summary>双击在「适应屏幕（1×）」与「2×」之间切换，焦点取双击点。</summary>
    public bool OnDoubleTap(MotionEvent? e)
    {
        if (e is null || _bitmap is null)
        {
            return true;
        }

        if (_userScale > MinScale + Epsilon)
        {
            ZoomTo(MinScale, e.GetX(), e.GetY());
        }
        else
        {
            ZoomTo(2f, e.GetX(), e.GetY());
        }

        return true;
    }

    public bool OnDoubleTapEvent(MotionEvent? e) => false;

    public bool OnSingleTapConfirmed(MotionEvent? e) => false;

    /// <summary>按速度起一段衰减平移（减速插值器），让放大态下的甩动有惯性收尾。</summary>
    private void StartFling(float velocityX, float velocityY)
    {
        _flingAnimator?.Cancel();
        var speed = (float)Math.Sqrt(velocityX * velocityX + velocityY * velocityY);
        // 速度换算成 ms：经验区间 150–600ms，太快收不住、太慢没惯性感。
        var duration = Math.Clamp(speed * 0.2f, 150f, 600f);
        var anim = ValueAnimator.OfFloat(0f, 1f)!;
        anim.SetDuration((long)duration);
        anim.SetInterpolator(new DecelerateInterpolator());

        // 位移正比于速度：向右甩（vx>0）图就继续向右走（transX 增大），所以不取反。
        var k = duration / 1000f * 0.25f;
        _flingAnimator = anim;
        anim.AddUpdateListener(new FlingUpdateListener(this, _transX, _transY, velocityX * k, velocityY * k));
        anim.Start();
    }

    /// <summary>未放大态的拖动模式裁决。</summary>
    private enum DragMode
    {
        None,
        VerticalClose,
        HorizontalSwitch,
    }

    /// <summary>整页内容容器（舞台 + 上下渐变遮罩），仅构建一次。</summary>
    private FrameLayout _chrome = null!;

    /// <summary>惯性平移的逐帧回调：把 (start + delta·t) 写回平移并夹边界。</summary>
    private sealed class FlingUpdateListener : Java.Lang.Object, ValueAnimator.IAnimatorUpdateListener
    {
        private readonly LightboxView _owner;
        private readonly float _startX;
        private readonly float _startY;
        private readonly float _dx;
        private readonly float _dy;

        internal FlingUpdateListener(LightboxView owner, float startX, float startY, float dx, float dy)
        {
            _owner = owner;
            _startX = startX;
            _startY = startY;
            _dx = dx;
            _dy = dy;
        }

        public void OnAnimationUpdate(ValueAnimator? animator)
        {
            if (animator is null)
            {
                return;
            }

            var t = animator.AnimatedFraction;
            _owner._transX = _startX + _dx * t;
            _owner._transY = _startY + _dy * t;
            _owner.ClampAndApply();
        }
    }

    /// <summary>下滑未达阈值时整片回弹归位的逐帧回调。</summary>
    private sealed class SheetUpdateListener : Java.Lang.Object, ValueAnimator.IAnimatorUpdateListener
    {
        private readonly LightboxView _owner;
        private readonly float _fromTy;
        private readonly float _toTy;
        private readonly float _fromA;
        private readonly float _toA;

        internal SheetUpdateListener(LightboxView owner, float fromTy, float toTy, float fromA, float toA)
        {
            _owner = owner;
            _fromTy = fromTy;
            _toTy = toTy;
            _fromA = fromA;
            _toA = toA;
        }

        public void OnAnimationUpdate(ValueAnimator? animator)
        {
            if (animator is null)
            {
                return;
            }

            var t = animator.AnimatedFraction;
            _owner.TranslationY = _fromTy + (_toTy - _fromTy) * t;
            _owner.Alpha = _fromA + (_toA - _fromA) * t;
        }
    }
}
