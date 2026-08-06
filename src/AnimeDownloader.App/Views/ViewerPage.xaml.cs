using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 查看器页：显示当前图源的随机大图，支持换一张 / 保存 / 全屏。
/// 图源与 NSFW 过滤来自主窗口全局工具栏，切换源后立即重载。
/// </summary>
public sealed partial class ViewerPage : Page, IModePage
{
    private MainWindow? _owner;
    private SettingsStore _settingsStore = null!;
    private ImageDownloader _downloader = null!;
    private IImageSource _currentSource = null!;
    private NsfwMode _nsfwMode = NsfwMode.BlockNsfw;
    private ImageItem? _current;
    private byte[]? _content;
    private int _loadToken;

    public ViewerPage()
    {
        InitializeComponent();
    }

    // ---------------- IModePage ----------------

    public void Attach(MainWindow owner)
    {
        _owner = owner;
        _settingsStore = owner.SettingsStore;
        _downloader = owner.Downloader;
        _currentSource = owner.CurrentSource;
        _nsfwMode = owner.CurrentNsfwMode;
        // 无论是否首次进入，只要尚未显示图片就尝试加载
        if (_current is null)
        {
            _ = LoadRandomAsync();
        }
    }

    public void OnSourceChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _currentSource = _owner.CurrentSource;
        _ = LoadRandomAsync();
    }

    public void OnNsfwChanged()
    {
        if (_owner is null)
        {
            return;
        }

        _nsfwMode = _owner.CurrentNsfwMode;
        _ = LoadRandomAsync();
    }

    public void Reload() => _ = LoadRandomAsync();

    private async Task LoadRandomAsync()
    {
        var token = ++_loadToken;
        LoadingRing.IsActive = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ViewImage.Source = null;
        StatusText.Text = "加载中…";
        try
        {
            var item = await _currentSource.GetRandomImageAsync(_nsfwMode);
            if (token != _loadToken)
            {
                return;
            }

            if (item is null)
            {
                ErrorText.Text = "没有获取到图片。可尝试更换图源或 NSFW 模式。";
                ErrorPanel.Visibility = Visibility.Visible;
                StatusText.Text = "加载失败";
                return;
            }

            _current = item;
            _content = await _downloader.DownloadAsync(item.Url);
            if (token != _loadToken)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.SetSource(new MemoryStream(_content).AsRandomAccessStream());
            ViewImage.Source = bitmap;
            MetaText.Text = string.Join(" · ", new[]
            {
                _currentSource.DisplayName,
                item.Artist ?? string.Empty,
                item.SourceLink ?? string.Empty,
            }.Where(s => s.Length > 0));
            StatusText.Text = "就绪";
        }
        catch (Exception ex)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = $"加载失败：{ex.Message}";
            ErrorPanel.Visibility = Visibility.Visible;
            StatusText.Text = "加载失败";
        }
        finally
        {
            if (token == _loadToken)
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        var presenter = _owner?.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter is null)
        {
            return;
        }

        if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            FullscreenButton.Content = "全屏";
        }
        else
        {
            presenter.Maximize();
            FullscreenButton.Content = "还原";
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null || _owner is null)
        {
            return;
        }

        if (_content is null)
        {
            try
            {
                _content = await _downloader.DownloadAsync(_current.Url);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"保存失败：{ex.Message}";
                return;
            }
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        var ext = _current.Extension ?? "png";
        picker.FileTypeChoices.Add("图片", new List<string> { $".{ext}" });
        picker.SuggestedFileName = SuggestFileName(_current);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await Windows.Storage.FileIO.WriteBytesAsync(file, _content);
        StatusText.Text = "已保存";
    }

    private static string SuggestFileName(ImageItem item)
    {
        var baseName = string.IsNullOrWhiteSpace(item.Id)
            ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
            : item.Id;
        return $"{baseName}.{item.Extension ?? "png"}";
    }
}
