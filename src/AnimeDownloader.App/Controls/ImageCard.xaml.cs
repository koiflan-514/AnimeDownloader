using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 缩略图卡片：异步加载缩略图（共享缓存 + 有界解码），加载中显示微光扫过占位，
/// 悬浮放大并高亮、显示「打开 / 保存」快捷操作；支持选中态（边缘亮起）、
/// 双击 / 回车打开大图、错峰入场动画。
/// </summary>
public sealed partial class ImageCard : UserControl, IDisposable
{
    private const int ThumbnailDecodePixelWidth = 360;

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(ImageItem),
            typeof(ImageCard),
            new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty DownloaderProperty =
        DependencyProperty.Register(
            nameof(Downloader),
            typeof(ImageDownloader),
            typeof(ImageCard),
            new PropertyMetadata(null, OnDownloaderChanged));

    private int _loadToken;
    private bool _loading;
    private bool _entrancePlayed;
    private CancellationTokenSource? _thumbnailCts;
    private Storyboard? _shimmerStoryboard;
    private Storyboard? _hoverStoryboard;

    /// <summary>可选缩放代理模板，透传给缩略图解析器。</summary>
    public string? ThumbnailProxyTemplate { get; set; }

    /// <summary>入场动画延迟（画廊按序错峰）；0 表示直接显示。</summary>
    public TimeSpan EntranceDelay { get; set; }

    /// <summary>悬浮「打开大图」请求（携带当前条目）。</summary>
    public event EventHandler<ImageItem>? OpenRequested;

    /// <summary>悬浮快捷保存请求（携带当前条目）。</summary>
    public event EventHandler<ImageItem>? QuickSaveRequested;

    public ImageCard()
    {
        InitializeComponent();
        Loaded += OnCardLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnCardSizeChanged;
    }

    /// <summary>
    /// 把图片与占位层强制锁定为卡片实际尺寸。
    /// GridViewItem 的测量可能给子内容无限高度约束，导致 Image 按位图自然比例
    /// 溢出卡片（卡片看起来矮、边缘出现黑边/串色）；显式定宽高可杜绝溢出。
    /// </summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Thumb.Width = width;
        Thumb.Height = height;
        ShimmerHost.Width = width;
        ShimmerHost.Height = height;
    }

    public ImageItem? Item
    {
        get => (ImageItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public ImageDownloader? Downloader
    {
        get => (ImageDownloader?)GetValue(DownloaderProperty);
        set => SetValue(DownloaderProperty, value);
    }

    /// <summary>
    /// 在卡片加入视觉树前锁定尺寸：避免 Image 按位图纵横比参与测量，
    /// 导致 GridViewItem 容器高度漂移（首卡变矮/偏移）。
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

    private void OnCardLoaded(object sender, RoutedEventArgs e)
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

    private void PlayEntranceAnimation()
    {
        CardVisual.Opacity = 0;
        CardTranslate.Y = 14;
        var storyboard = new Storyboard { BeginTime = EntranceDelay };
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, CardVisual);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            From = 14,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, CardTranslate);
        Storyboard.SetTargetProperty(slide, "Y");
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ImageCard)d).LoadThumbnail();
    }

    private static void OnDownloaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ImageCard)d;
        if (card.Item is not null && card.Thumb.Source is null && !card._loading)
        {
            card.LoadThumbnail();
        }
    }

    private void StartShimmer()
    {
        if (_shimmerStoryboard is not null)
        {
            return;
        }

        var width = ShimmerHost.ActualWidth;
        if (width <= 0)
        {
            ShimmerHost.Loaded += (_, _) => StartShimmer();
            return;
        }

        ShimmerTranslate.X = -ShimmerSlide.ActualWidth;
        var anim = new DoubleAnimation
        {
            From = -ShimmerSlide.ActualWidth,
            To = width + ShimmerSlide.ActualWidth,
            Duration = TimeSpan.FromMilliseconds(1500),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, ShimmerTranslate);
        Storyboard.SetTargetProperty(anim, "X");
        _shimmerStoryboard = new Storyboard();
        _shimmerStoryboard.Children.Add(anim);
        _shimmerStoryboard.Begin();
    }

    private void StopShimmer()
    {
        _shimmerStoryboard?.Stop();
        _shimmerStoryboard = null;
        ShimmerHost.Visibility = Visibility.Collapsed;
    }

    private async void LoadThumbnail()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var cancellationToken = _thumbnailCts.Token;
        var token = ++_loadToken;
        _loading = true;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        Thumb.Source = null;
        Thumb.Visibility = Visibility.Collapsed;
        ArtistScrim.Visibility = Visibility.Collapsed;
        ResBadge.Visibility = Visibility.Collapsed;
        ShimmerHost.Visibility = Visibility.Visible;
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
            ResBadge.Visibility = Visibility.Visible;
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
                ErrorOverlay.Visibility = Visibility.Visible;
                StopShimmer();
                return;
            }

            var bytes = await downloader.DownloadCachedAsync(targetUrl, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (token != _loadToken)
            {
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = Math.Clamp((int)Math.Ceiling(ActualWidth * 2), 180, ThumbnailDecodePixelWidth),
                DecodePixelType = DecodePixelType.Logical,
            };
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
            Thumb.Source = bitmap;
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                Thumb.Width = ActualWidth;
                Thumb.Height = ActualHeight;
            }

            Thumb.Visibility = Visibility.Visible;
            ArtistScrim.Visibility = string.IsNullOrEmpty(Item.Artist)
                ? Visibility.Collapsed
                : Visibility.Visible;
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
            ErrorOverlay.Visibility = Visibility.Visible;
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

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        PlayHover(scale: 1.04, showActions: true);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        PlayHover(scale: 1, showActions: false);
        ProtectedCursor = null;
    }

    private void PlayHover(double scale, bool showActions)
    {
        if (_hoverStoryboard is not null)
        {
            _hoverStoryboard.Stop();
        }

        _hoverStoryboard = new Storyboard();
        var scaleAnim = new DoubleAnimation
        {
            From = CardScale.ScaleX,
            To = scale,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(scaleAnim, CardScale);
        Storyboard.SetTargetProperty(scaleAnim, "ScaleX");
        _hoverStoryboard.Children.Add(scaleAnim);

        var scaleYAnim = new DoubleAnimation
        {
            From = CardScale.ScaleY,
            To = scale,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(scaleYAnim, CardScale);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        _hoverStoryboard.Children.Add(scaleYAnim);

        _hoverStoryboard.Begin();
        HoverActions.Visibility = showActions && Item is not null ? Visibility.Visible : Visibility.Collapsed;
        HoverActions.Opacity = showActions ? 1 : 0;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            OpenRequested?.Invoke(this, Item);
        }
    }

    private void OnQuickSave(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            QuickSaveRequested?.Invoke(this, Item);
        }
    }

    private void OnCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Item is not null)
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, Item);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _thumbnailCts?.Cancel();
        _shimmerStoryboard?.Stop();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        _shimmerStoryboard?.Stop();
    }
}