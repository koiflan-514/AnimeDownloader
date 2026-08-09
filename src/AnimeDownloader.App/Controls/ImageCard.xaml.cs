using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// Thumbnail card: downloads image bytes asynchronously (through the shared thumbnail cache),
/// decodes at a bounded pixel size for memory efficiency, and shows a hover scale plus an
/// artist caption overlay.
/// </summary>
public sealed partial class ImageCard : UserControl
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

    /// <summary>Optional resize proxy template passed through to the thumbnail resolver.</summary>
    public string? ThumbnailProxyTemplate { get; set; }

    public ImageCard()
    {
        InitializeComponent();
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

    private async void LoadThumbnail()
    {
        var token = ++_loadToken;
        _loading = true;
        LoadingRing.IsActive = true;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        Thumb.Source = null;
        ArtistOverlay.Visibility = Visibility.Collapsed;

        if (Item is null)
        {
            _loading = false;
            LoadingRing.IsActive = false;
            return;
        }

        ArtistText.Text = Item.Artist ?? string.Empty;
        var downloader = Downloader;
        if (downloader is null)
        {
            _loading = false;
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
                return;
            }

            var bytes = await downloader.DownloadCachedAsync(targetUrl).ConfigureAwait(true);
            if (token != _loadToken)
            {
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = ThumbnailDecodePixelWidth,
                DecodePixelType = DecodePixelType.Logical,
            };
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
            Thumb.Source = bitmap;
            ArtistOverlay.Visibility = string.IsNullOrEmpty(Item.Artist)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = "加载失败";
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

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CardScale.ScaleX = 1.04;
        CardScale.ScaleY = 1.04;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        ProtectedCursor = null;
    }
}
