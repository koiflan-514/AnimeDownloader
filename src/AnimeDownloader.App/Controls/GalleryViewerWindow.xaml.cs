using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// Full-window viewer for a gallery item list: previous/next with keyboard support, background
/// preload of the next image, save with progress and fullscreen toggle.
/// </summary>
public sealed partial class GalleryViewerWindow : Window
{
    private readonly IReadOnlyList<ImageItem> _items;
    private readonly MainWindow _owner;
    private readonly ImageDownloader _downloader;
    private int _index;
    private int _loadToken;
    private byte[]? _content;
    private string? _nextUrl;
    private byte[]? _nextContent;
    private bool _isSaving;
    private int _windowThumbToken;

    public GalleryViewerWindow(MainWindow owner, IReadOnlyList<ImageItem> items, int index)
    {
        InitializeComponent();
        _owner = owner;
        _items = items;
        _index = index;
        _downloader = owner.Downloader;
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
        Win11Chrome.SetIcon(this);
        Win11Chrome.Apply(this, Root);
        Root.Loaded += (_, _) => Win11Chrome.Apply(this, Root);
        AppWindow.Resize(new SizeInt32(960, 680));
        ShowItem();
        if (NextButton.IsEnabled)
        {
            NextButton.Focus(FocusState.Programmatic);
        }
        else if (PrevButton.IsEnabled)
        {
            PrevButton.Focus(FocusState.Programmatic);
        }
        else
        {
            SaveButton.Focus(FocusState.Programmatic);
        }
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
        _owner.SetHeaderThumbnail(_items[_index]);
        _ = LoadWindowThumbAsync(_items[_index]);
        _ = LoadImageAsync(_items[_index].Url);
        _ = PreloadNextAsync();
    }

    private async Task LoadWindowThumbAsync(ImageItem item)
    {
        var token = ++_windowThumbToken;
        var thumbUrl = ThumbnailResolver.ResolveThumbnailUrl(
            item.ThumbnailUrl,
            item.Url,
            _owner.Settings.ThumbnailProxyTemplate);
        if (thumbUrl is null)
        {
            WindowThumb.Source = null;
            WindowThumbBorder.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bytes = await _downloader.DownloadCachedAsync(thumbUrl);
            if (token != _windowThumbToken)
            {
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = 80,
                DecodePixelType = DecodePixelType.Logical,
            };
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
            WindowThumb.Source = bitmap;
            WindowThumbBorder.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (token == _windowThumbToken)
            {
                WindowThumb.Source = null;
                WindowThumbBorder.Visibility = Visibility.Collapsed;
            }
        }
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
            byte[] bytes;
            if (_nextUrl == url && _nextContent is not null)
            {
                bytes = _nextContent;
                _nextUrl = null;
                _nextContent = null;
            }
            else
            {
                bytes = await _downloader.DownloadAsync(url);
            }

            if (token != _loadToken)
            {
                return;
            }

            _content = bytes;
            var bitmap = new BitmapImage();
            bitmap.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
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

    private async Task PreloadNextAsync()
    {
        if (_index >= _items.Count - 1)
        {
            return;
        }

        var nextItem = _items[_index + 1];
        try
        {
            _nextContent = await _downloader.DownloadAsync(nextItem.Url);
            _nextUrl = nextItem.Url;
        }
        catch (Exception)
        {
            // Preload is best-effort; navigation will download on demand.
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

    private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.PageUp:
                e.Handled = true;
                OnPrev(sender, new RoutedEventArgs());
                break;
            case VirtualKey.Right:
            case VirtualKey.PageDown:
            case VirtualKey.Space:
                e.Handled = true;
                OnNext(sender, new RoutedEventArgs());
                break;
            case VirtualKey.F11:
                e.Handled = true;
                OnToggleFullscreen(sender, new RoutedEventArgs());
                break;
            case VirtualKey.Escape:
                if (AppWindow.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
                {
                    e.Handled = true;
                    ExitFullscreen();
                }

                break;
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
        {
            ExitFullscreen();
        }
        else
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            FullscreenButton.Content = "退出全屏";
            Toolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void ExitFullscreen()
    {
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        FullscreenButton.Content = "全屏";
        Toolbar.Visibility = Visibility.Visible;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_index < 0 || _index >= _items.Count || _isSaving)
        {
            return;
        }

        _isSaving = true;
        SaveButton.IsEnabled = false;
        try
        {
            var item = _items[_index];
            if (_content is null)
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var percent = p.Percent is { } value ? (int)(value * 100) : 0;
                    SaveButton.Content = percent > 0 ? $"保存中 {percent}%" : "保存中…";
                });
                _content = await _downloader.DownloadAsync(item.Url, progress);
            }

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("图片", new List<string> { $".{item.Extension ?? "png"}" });
            picker.SuggestedFileName = item.SuggestFileName();

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await Windows.Storage.FileIO.WriteBytesAsync(file, _content);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"保存失败：{ex.Message}";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            SaveButton.Content = "保存";
            SaveButton.IsEnabled = true;
            _isSaving = false;
        }
    }
}
