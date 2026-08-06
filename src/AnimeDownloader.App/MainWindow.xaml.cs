using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeDownloader.App;

/// <summary>
/// 主窗口：持有应用级共享状态（设置、图源、HttpClient、图片下载器），
/// 通过 NavigationView 在画廊 / 查看器 / 设置三页间切换。
/// 页面通过 <see cref="NavigationViewItem.Tag"/> 导航，页面经 Frame.Navigate
/// 的 Parameter 拿到本窗口引用以访问共享依赖。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private HttpClient _http = null!;
    private IReadOnlyList<IImageSource> _sources = null!;
    private ImageDownloader _downloader = null!;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = App.SettingsStore;
        RebuildHttp();
        Title = "AnimeDownloader";
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
    }

    /// <summary>按当前设置重建 HttpClient、图源与下载器（代理/直连变更后调用）。</summary>
    private void RebuildHttp()
    {
        var settings = App.Settings;
        var factory = new HttpClientFactory(settings);
        var http = factory.CreateClient();
        var sources = SourceRegistry.CreateAll(http);
        SourceRegistry.ApplySettings(sources, settings);

        _http?.Dispose();
        _http = http;
        _sources = sources;
        _downloader = new ImageDownloader(_http);
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        RootNav.SelectedItem = GalleryNavItem;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "gallery";
        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "viewer" => typeof(ViewerPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(GalleryPage),
        };
        ContentFrame.Navigate(pageType, this);
    }

    /// <summary>当前全部图源实例。</summary>
    public IReadOnlyList<IImageSource> Sources => _sources;

    /// <summary>图片下载器（保存 / 批量下载）。</summary>
    public ImageDownloader Downloader => _downloader;

    /// <summary>设置存储。</summary>
    public SettingsStore SettingsStore => _settingsStore;

    /// <summary>设置页保存后调用：重载内存设置、重建网络层（代理生效）、应用主题。</summary>
    public void ApplySettings()
    {
        App.Settings = _settingsStore.Load();
        RebuildHttp();
        Root.RequestedTheme = App.ResolveTheme(App.Settings.Theme);
    }
}
