using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 画廊全屏查看器：浏览缩略图列表中的大图，支持上一张 / 下一张、
/// 保存与全屏切换。对应参考项目的 GalleryViewerWindow。
/// </summary>
public sealed partial class GalleryViewerWindow : Window
{
    private readonly IReadOnlyList<ImageItem> _items;
    private readonly MainWindow _owner;
    private readonly ImageDownloader _downloader;
    private int _index;
    private int _loadToken;
    private byte[]? _content;

    public GalleryViewerWindow(MainWindow owner, IReadOnlyList<ImageItem> items, int index)
    {
        InitializeComponent();
        _owner = owner;
        _items = items;
        _index = index;
        _downloader = owner.Downloader;
        // 窗口居中
        AppWindow.Resize(new SizeInt32(900, 650));
        ShowItem();
    }

    private void ShowItem()
    {
        if (_items.Count == 0)
        {
            IndexLabel.Text = "0 / 0";
            return;
        }

        IndexLabel.Text = $"{_index + 1} / {_items.Count}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
        _ = LoadImageAsync(_items[_index].Url);
    }

    private async Task LoadImageAsync(string url)
    {
        var token = ++_loadToken;
        LoadingRing.IsActive = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ViewImage.Source = null;
        _content = null;
        try
        {
            _content = await _downloader.DownloadAsync(url);
            if (token != _loadToken)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.SetSource(new MemoryStream(_content).AsRandomAccessStream());
            ViewImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = $"加载失败：{ex.Message}";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            if (token == _loadToken)
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_index > 0)
        {
            _index--;
            ShowItem();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index < _items.Count - 1)
        {
            _index++;
            ShowItem();
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            FullscreenButton.Content = "全屏";
            Toolbar.Visibility = Visibility.Visible;
            return;
        }

        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        FullscreenButton.Content = "退出全屏";
        Toolbar.Visibility = Visibility.Collapsed;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_index < 0 || _index >= _items.Count)
        {
            return;
        }

        var item = _items[_index];
        // 若尚未下载过当前图，先下载（下载失败时给出提示）。
        if (_content is null)
        {
            try
            {
                _content = await _downloader.DownloadAsync(item.Url);
            }
            catch (Exception ex)
            {
                ErrorText.Text = $"保存失败：{ex.Message}";
                ErrorPanel.Visibility = Visibility.Visible;
                return;
            }
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("图片", new List<string> { $".{item.Extension ?? "png"}" });
        picker.SuggestedFileName = SuggestFileName(item);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await Windows.Storage.FileIO.WriteBytesAsync(file, _content);
    }

    private static string SuggestFileName(ImageItem item)
    {
        var baseName = string.IsNullOrWhiteSpace(item.Id)
            ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
            : item.Id;
        var ext = item.Extension ?? "png";
        return $"{baseName}.{ext}";
    }
}
