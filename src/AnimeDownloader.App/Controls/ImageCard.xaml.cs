using AnimeDownloader.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 缩略图卡片：异步加载图片，显示加载环，加载失败时显示占位图标。
/// </summary>
public sealed partial class ImageCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(ImageItem),
            typeof(ImageCard),
            new PropertyMetadata(null, OnItemChanged));

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

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ImageCard)d).LoadThumbnail();
    }

    private async void LoadThumbnail()
    {
        var token = ++_loadToken;
        LoadingRing.IsActive = true;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        Thumb.Source = null;

        if (Item is null)
        {
            LoadingRing.IsActive = false;
            return;
        }

        try
        {
            // BitmapImage 本身是异步解码；设置 UriSource 即可在后台线程加载。
            var bitmap = new BitmapImage();
            bitmap.ImageOpened += (_, _) =>
            {
                if (token != _loadToken)
                {
                    return;
                }

                LoadingRing.IsActive = false;
                Thumb.Source = bitmap;
            };
            bitmap.ImageFailed += (_, _) =>
            {
                if (token != _loadToken)
                {
                    return;
                }

                LoadingRing.IsActive = false;
                ErrorOverlay.Visibility = Visibility.Visible;
            };
            bitmap.UriSource = new Uri(Item.Url, UriKind.Absolute);
        }
        catch (Exception)
        {
            if (token != _loadToken)
            {
                return;
            }

            LoadingRing.IsActive = false;
            ErrorOverlay.Visibility = Visibility.Visible;
        }
    }
}
