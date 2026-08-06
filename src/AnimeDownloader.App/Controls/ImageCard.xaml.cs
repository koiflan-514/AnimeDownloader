using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 缩略图卡片：异步下载图片字节并解码显示（与查看器页一致的可信路径——
/// 未打包 WinUI3 应用中 BitmapImage 直接加载网络 URL 不可靠），
/// 加载期间显示加载环，失败时显示占位图标。
/// </summary>
public sealed partial class ImageCard : UserControl
{
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

    public ImageCard()
    {
        InitializeComponent();
    }

    /// <summary>绑定的图片条目；设置后触发异步加载。</summary>
    public ImageItem? Item
    {
        get => (ImageItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>共享图片下载器（由画廊页注入，避免每卡片建 HttpClient）。</summary>
    public ImageDownloader? Downloader
    {
        get => (ImageDownloader?)GetValue(DownloaderProperty);
        set => SetValue(DownloaderProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ImageCard)d).LoadThumbnail();
    }

    private static void OnDownloaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ImageCard)d;
        // 若 Item 已在且当前未成功显示（比如初始化时 Item 先于 Downloader 赋值，
        // 首次加载因缺下载器走了失败占位），Downloader 就绪后重新加载。
        if (card.Item is not null && card.Thumb.Source is null && !card._loading)
        {
            card.LoadThumbnail();
        }
    }

    private bool _loading;

    private async void LoadThumbnail()
    {
        var token = ++_loadToken;
        _loading = true;
        LoadingRing.IsActive = true;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        Thumb.Source = null;

        if (Item is null)
        {
            _loading = false;
            LoadingRing.IsActive = false;
            return;
        }

        var downloader = Downloader;
        if (downloader is null)
        {
            // 未注入下载器：复位加载标志并保持加载环，
            // 等待 Downloader 就绪后由 OnDownloaderChanged 回调重新加载。
            _loading = false;
            return;
        }

        try
        {
            var bytes = await downloader.DownloadAsync(Item.Url);
            if (token != _loadToken)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
            Thumb.Source = bitmap;
        }
        catch (Exception)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorOverlay.Visibility = Visibility.Visible;
        }
        finally
        {
            if (token == _loadToken)
            {
                _loading = false;
                LoadingRing.IsActive = false;
            }
        }
    }
}
