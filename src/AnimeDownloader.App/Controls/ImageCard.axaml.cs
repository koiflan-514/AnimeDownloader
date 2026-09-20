using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 缩略图卡片（相纸）：异步加载缩略图（共享缓存 + 有界解码），加载中显示微光扫过占位，
/// 悬浮放大并高亮、显示「打开 / 保存」快捷操作；支持回车打开、错峰入场动画。
///
/// 实现 <see cref="IDisposable"/> 是因为它持有两枚 <see cref="CancellationTokenSource"/>
/// （缩略图下载 + 微光动画）。这不是形式主义：卡片被移出面板时若只做「摘除」，
/// 在跑的下载与动画会继续活着，换几批图就攒下一堆。画廊在换批时显式调用
/// <see cref="Dispose"/>；只离开视口（Unloaded）不算回收，那时只暂停、不释放。
/// </summary>
public sealed partial class ImageCard : UserControl, IDisposable
{
    private const int ThumbnailDecodePixelWidth = 360;

    /// <summary>入场动画的位移起点（像素）。</summary>
    private const double EntranceOffset = 14;

    /// <summary>入场动画时长。</summary>
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(320);

    /// <summary>悬停放大倍数。1.04 正好吃掉瓦片里让出的栏距。</summary>
    private const double HoverScale = 1.04;

    public static readonly StyledProperty<ImageItem?> ItemProperty =
        AvaloniaProperty.Register<ImageCard, ImageItem?>(nameof(Item));

    public static readonly StyledProperty<ImageDownloader?> DownloaderProperty =
        AvaloniaProperty.Register<ImageCard, ImageDownloader?>(nameof(Downloader));

    /// <summary>
    /// 微光层的位移载体。必须真的挂到 <c>ShimmerSlide.RenderTransform</c> 上 ——
    /// Avalonia 12 的 TransformAnimator 是从**目标 Visual 的 RenderTransform** 里
    /// 取这个实例来写值的（TranslateTransform 自己没有回指 Visual 的成员）。
    /// </summary>
    private readonly TranslateTransform _shimmerTranslate = new();

    private int _loadToken;
    private bool _loading;
    private bool _entrancePlayed;
    private CancellationTokenSource? _thumbnailCts;

    /// <summary>正在播放的微光动画所挂的样式；非 null 即表示「微光正在扫」。</summary>
    private Style? _shimmerStyle;

    public ImageCard()
    {
        InitializeComponent();

        ShimmerSlide.RenderTransform = _shimmerTranslate;
        Loaded += OnCardLoaded;
        Unloaded += OnCardUnloaded;
        SizeChanged += OnCardSizeChanged;
    }

    /// <summary>可选缩放代理模板，透传给缩略图解析器。</summary>
    public string? ThumbnailProxyTemplate { get; set; }

    /// <summary>入场动画延迟（画廊按序错峰）；0 表示直接显示、不参与动画。</summary>
    public TimeSpan EntranceDelay { get; set; }

    /// <summary>悬浮「打开大图」请求（携带当前条目）。</summary>
    public event EventHandler<ImageItem>? OpenRequested;

    /// <summary>悬浮快捷保存请求（携带当前条目）。</summary>
    public event EventHandler<ImageItem>? QuickSaveRequested;

    /// <summary>当前绑定的条目。</summary>
    public ImageItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>共享下载器（提供缩略图缓存）。</summary>
    public ImageDownloader? Downloader
    {
        get => GetValue(DownloaderProperty);
        set => SetValue(DownloaderProperty, value);
    }

    /// <summary>
    /// 在卡片加入视觉树前锁定尺寸：避免 Image 按位图纵横比参与测量，
    /// 导致瓦片容器高度漂移（首卡变矮/偏移）。
    /// </summary>
    public void SetCardSize(double size)
    {
        if (size <= 0)
        {
            return;
        }

        Width = size;
        Height = size;
        Thumb.Width = size;
        Thumb.Height = size;
        ShimmerHost.Width = size;
        ShimmerHost.Height = size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemProperty)
        {
            LoadThumbnail();
        }
        else if (change.Property == DownloaderProperty &&
                 Item is not null && Thumb.Source is null && !_loading)
        {
            LoadThumbnail();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter && Item is not null)
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, Item);
        }
    }

    /// <summary>
    /// 单击整张卡片 = 打开大图。
    ///
    /// 这一条在 WinUI 版里是 <c>GridView.ItemClick</c> 提供的（还带
    /// <c>IsItemClickEnabled</c>）。Avalonia 没有 GridView，画廊改用
    /// <c>TileWrapPanel</c> 直接承载卡片，于是「点卡片要做什么」自然落回卡片自己身上。
    ///
    /// 用 <c>PointerPressed</c> 而不是 <c>Tapped</c>：与 WinUI 的 ItemClick 时机一致
    /// （按下即响应，不等抬起），也避免与卡片内两枚悬浮按钮的 Click 抢时序。
    /// 落在悬浮按钮上的按压直接放行 —— 那两枚按钮有自己的 Click。
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Item is null || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed is false)
        {
            return;
        }

        if (IsOverlayButton(e.Source as Visual))
        {
            return;
        }

        Focus();
        e.Handled = true;
        OpenRequested?.Invoke(this, Item);
    }

    /// <summary>按压点是否落在卡片内的悬浮快捷按钮上（那两枚按钮各自处理自己的点击）。</summary>
    private bool IsOverlayButton(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, OpenButton) || ReferenceEquals(visual, SaveButton))
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        PlayHover();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ResetHover();
    }

    /// <summary>
    /// 把图片与占位层强制锁定为卡片实际尺寸。
    /// 瓦片容器可能给子内容无限高度约束，导致 Image 按位图自然比例溢出卡片
    /// （卡片看起来矮、边缘出现黑边/串色）；显式定宽高可杜绝溢出。
    /// </summary>
    private void OnCardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        Thumb.Width = size.Width;
        Thumb.Height = size.Height;
        ShimmerHost.Width = size.Width;
        ShimmerHost.Height = size.Height;
    }

    private void OnCardLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_entrancePlayed && EntranceDelay > TimeSpan.Zero)
        {
            _entrancePlayed = true;
            PlayEntranceAnimation();
        }

        if (Item is not null && Thumb.Source is null && !_loading)
        {
            LoadThumbnail();
        }
    }

    private void OnCardUnloaded(object? sender, RoutedEventArgs e)
    {
        _thumbnailCts?.Cancel();
        StopShimmer();
    }

    // ---------------- 动画 ----------------

    /// <summary>
    /// 错峰入场：先无动画地摆到「未显影」位置（透明 + 下移 14px），
    /// 到点后再挂上过渡并推到终点，于是那一段过渡被真正播出来。
    ///
    /// 为什么不在 XAML 里声明这段过渡：延迟逐卡不同（按序号 × 28ms），
    /// 静态 XAML 表达不了「每张卡一个延迟」。而如果在 XAML 里声明过渡、
    /// 又在 Loaded 里设初始值，就会先播一次「淡出」再等延迟淡入 ——
    /// 因为过渡是在元素挂上可视树时才生效的，那时属性已经是终值了。
    /// </summary>
    private void PlayEntranceAnimation()
    {
        CardVisual.Opacity = 0;
        CardVisual.RenderTransform = TransformOperations.Parse($"translate(0px, {EntranceOffset}px)");

        DispatcherTimer.RunOnce(
            () =>
            {
                CardVisual.Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = EntranceDuration,
                        // Avalonia 12 的缓动是一族「按方向拆开」的类，没有 EasingMode 属性：
                        // WinUI 的 CubicEase{EasingMode=EaseOut} 对应这里的 CubicEaseOut。
                        Easing = new CubicEaseOut(),
                    },
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = EntranceDuration,
                        Easing = new CubicEaseOut(),
                    },
                };
                CardVisual.Opacity = 1;
                CardVisual.RenderTransform = TransformOperations.Identity;
            },
            EntranceDelay);
    }

    private void PlayHover()
    {
        // 变换只驱动缩放；过渡在 XAML 里声明（见 CardRoot.Transitions）。
        CardRoot.RenderTransform = TransformOperations.Parse($"scale({HoverScale})");
        HoverActions.IsVisible = Item is not null;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void ResetHover()
    {
        CardRoot.RenderTransform = TransformOperations.Identity;
        HoverActions.IsVisible = false;
        Cursor = Cursor.Default;
    }

    /// <summary>
    /// 微光扫过。WinUI 版用 Storyboard + RepeatBehavior.Forever；
    /// Avalonia 用 Animation + 无限迭代，语义等价。
    ///
    /// 这里的写法有三处反直觉，都是踩坑 + 反射实测换来的（不是猜）：
    ///
    /// 1. setter 必须写 <c>TranslateTransform.XProperty</c> 这类属性，
    ///    这样 Animation 才会选中 <c>TransformAnimator</c>
    ///    （实测 <c>Animation.GetAnimatorType(TranslateTransform.XProperty)</c>
    ///    返回它；它在注册表里排在 DoubleAnimator 之前，只有 Transform 自己的属性命中它）。
    /// 2. 动画的**目标必须是持有该 Transform 的 Visual**，不能是 TranslateTransform 自己 ——
    ///    TransformAnimator 的 Apply 第一件事是 <c>(Visual)control</c>，传 Transform 进去
    ///    必定 InvalidCastException，而这个异常会顺着 UI 线程未处理路径把整个进程带走
    ///    （本机实测：画廊首屏出图那一刻必崩，且没有任何可见提示）。
    ///    它写值的方式是：从目标 Visual 的 RenderTransform 里取出那个实例再改 X。
    /// 3. 也不能绕道去动画 <c>Visual.RenderTransformProperty</c>：实测
    ///    <c>GetAnimatorType(RenderTransformProperty)</c> 返回 null（它的类型是 ITransform，
    ///    注册表里只有 TransformOperations 那一档），会抛「No animator registered」。
    /// 4. 循环动画**不能**用 <c>Animation.RunAsync</c> 播放：直接抛
    ///    InvalidOperationException("Looping animations must not use the Run method.")，
    ///    而且是从返回的 Task 里抛出来的（不 await 就变成「未观察异常」，只在崩溃日志里留一行）。
    ///    循环动画的载体是样式 —— 挂进控件的 Styles 即开始，取下来即停。
    /// </summary>
    private void StartShimmer()
    {
        if (_shimmerStyle is not null)
        {
            return;
        }

        var hostWidth = ShimmerHost.Bounds.Width;
        if (hostWidth <= 0)
        {
            // 布局还没算出来：等它一次。
            ShimmerHost.LayoutUpdated += RetryShimmer;
            return;
        }

        var slideWidth = ShimmerSlide.Bounds.Width > 0 ? ShimmerSlide.Bounds.Width : 360;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1500),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(TranslateTransform.XProperty, -slideWidth) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(TranslateTransform.XProperty, hostWidth + slideWidth) },
                },
            },
        };

        // 挂上样式即开始播放（见上面第 4 条：循环动画只能这样跑）。
        // 选择器限定 Border：这份样式住在 ShimmerSlide 自己的 Styles 里，
        // 会作用到它自己和后代，限定类型可以避免误伤内部其它元素。
        _shimmerStyle = new Style(x => x.OfType<Border>())
        {
            Animations = { animation },
        };
        ShimmerSlide.Styles.Add(_shimmerStyle);
    }

    private void RetryShimmer(object? sender, EventArgs e)
    {
        ShimmerHost.LayoutUpdated -= RetryShimmer;
        if (_shimmerStyle is null && ShimmerHost.IsVisible)
        {
            StartShimmer();
        }
    }

    private void StopShimmer()
    {
        if (_shimmerStyle is not null)
        {
            ShimmerSlide.Styles.Remove(_shimmerStyle);
            _shimmerStyle = null;
        }

        // 位移复位：不然下一次播放会从上一轮停下的位置接着走。
        _shimmerTranslate.X = 0;
        ShimmerHost.IsVisible = false;
    }

    // ---------------- 缩略图 ----------------

    private async void LoadThumbnail()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var cancellationToken = _thumbnailCts.Token;
        var token = ++_loadToken;
        _loading = true;

        ErrorOverlay.IsVisible = false;
        Thumb.Source = null;
        Thumb.IsVisible = false;
        ArtistScrim.IsVisible = false;
        ResBadge.IsVisible = false;
        ShimmerHost.IsVisible = true;
        ShimmerHost.Opacity = 1;
        StartShimmer();

        if (Item is null)
        {
            _loading = false;
            StopShimmer();
            return;
        }

        ArtistText.Text = Item.Artist ?? string.Empty;
        if (Item.Dimensions is { } dims)
        {
            ResText.Text = $"{dims.Width} × {dims.Height}";
            ResBadge.IsVisible = true;
        }

        var downloader = Downloader;
        if (downloader is null)
        {
            _loading = false;
            StopShimmer();
            return;
        }

        try
        {
            var targetUrl = ThumbnailResolver.ResolveThumbnailUrl(
                Item.ThumbnailUrl,
                Item.Url,
                ThumbnailProxyTemplate);
            if (targetUrl is null)
            {
                ErrorText.Text = "无缩略图";
                ErrorOverlay.IsVisible = true;
                StopShimmer();
                return;
            }

            var bytes = await downloader.DownloadCachedAsync(targetUrl, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (token != _loadToken)
            {
                return;
            }

            // 有界解码：目标最多 360 逻辑像素宽（×2 覆盖高 DPI），下限 180 避免小卡糊掉。
            // WinUI 那边是 BitmapImage.DecodePixelWidth；Avalonia 对应 Bitmap.DecodeToWidth。
            var decodeWidth = Math.Clamp((int)Math.Ceiling(Bounds.Width * 2), 180, ThumbnailDecodePixelWidth);
            using var stream = new MemoryStream(bytes);
            Thumb.Source = Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality);

            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                Thumb.Width = Bounds.Width;
                Thumb.Height = Bounds.Height;
            }

            Thumb.IsVisible = true;
            ArtistScrim.IsVisible = !string.IsNullOrEmpty(Item.Artist);
            StopShimmer();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 卡片离开视口或被重新绑定；不显示错误。
        }
        catch (Exception)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = "加载失败";
            ErrorOverlay.IsVisible = true;
            StopShimmer();
        }
        finally
        {
            if (token == _loadToken)
            {
                _loading = false;
            }
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            OpenRequested?.Invoke(this, Item);
        }
    }

    private void OnQuickSave(object? sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            QuickSaveRequested?.Invoke(this, Item);
        }
    }

    // ---------------- 回收 ----------------

    /// <summary>
    /// 释放卡片持有的取消令牌源（在跑的缩略图下载会被取消，微光动画停掉）。
    /// 由画廊在换批时调用。释放后卡片不应再用 —— 需要继续用的话重新 new 一张。
    /// </summary>
    public void Dispose()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        // StopShimmer 内部会 cancel + dispose 微光那枚，并把占位层收起来。
        StopShimmer();
        GC.SuppressFinalize(this);
    }
}
