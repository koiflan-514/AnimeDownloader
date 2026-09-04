using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 设置页：NSFW 过滤、主题、刷新间隔、画廊数量、代理 / 超时、下载目录、并发数、
/// 缩略图缓存与各图源标签。保存后通过 <see cref="MainWindow.ApplySettings"/> 立即生效。
/// </summary>
public sealed partial class SettingsPage : Page, IModePage
{
    private MainWindow? _owner;
    private SettingsStore? _store;
    private AppSettings? _settings;
    private IReadOnlyList<IImageSource> _sources = Array.Empty<IImageSource>();
    private readonly Dictionary<string, TextBox> _tagBoxes = new();

    /// <summary>探测结果行：状态用 Fluent 图标 + 系统语义色，不再使用 emoji。</summary>
    private sealed record ProbeRow(string StatusGlyph, Brush StatusBrush, string DisplayName, string StatusText);

    private static Brush SystemBrush(string key)
    {
        try
        {
            return (Brush)Application.Current.Resources[key];
        }
        catch (Exception)
        {
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }

    public SettingsPage()
    {
        InitializeComponent();
        NsfwSegmented.ItemsSource = new List<string> { "屏蔽 NSFW", "仅 NSFW", "全部显示" };
        ThemeSegmented.ItemsSource = new List<string> { "跟随系统", "浅色", "深色" };
    }

    // ---------------- IModePage ----------------

    public void Attach(MainWindow owner)
    {
        _owner = owner;
        _store = owner.SettingsStore;
        _settings = owner.Settings;
        _sources = owner.Sources;

        NsfwSegmented.SelectedIndex = _settings.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => 1,
            NsfwModeStorage.ShowEverything => 2,
            _ => 0,
        };
        ThemeSegmented.SelectedIndex = _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        IntervalBox.Value = _settings.AutoReloadIntervalSeconds;
        CountBox.Value = _settings.GalleryCount;
        AutoReloadCheck.IsOn = _settings.AutoReloadEnabled;
        ProxyBox.Text = _settings.ProxyUrl ?? string.Empty;
        NoProxyCheck.IsChecked = _settings.UseNoProxy;
        TimeoutBox.Value = _settings.RequestTimeoutSeconds;
        GelbooruUserIdBox.Text = _settings.GelbooruUserId ?? string.Empty;
        GelbooruApiKeyBox.Text = _settings.GelbooruApiKey ?? string.Empty;
        DownloadDirBox.Text = _settings.DownloadDirectory ?? string.Empty;
        ConcurrencyBox.Value = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 16);
        CacheToggle.IsOn = _settings.EnableThumbnailCache;
        ProxyTemplateBox.Text = _settings.ThumbnailProxyTemplate ?? string.Empty;
        WarningBar.IsOpen = false;

        TagsPanel.Children.Clear();
        _tagBoxes.Clear();
        foreach (var source in _sources.Where(s => s.SupportsTags))
        {
            var label = new TextBlock
            {
                Text = $"{source.DisplayName} 标签",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, 4, 0, 0),
            };
            var box = new TextBox
            {
                PlaceholderText = "空格分隔，例如：cat_ears solo 1girl",
            };
            box.Text = SettingsStore.GetSourceTags(_settings, source.Id);
            _tagBoxes[source.Id] = box;
            TagsPanel.Children.Add(label);
            TagsPanel.Children.Add(box);
        }
    }

    public void OnSourceChanged()
    {
    }

    public void OnNsfwChanged()
    {
    }

    public void Reload()
    {
    }

    private void OnBrowseDir(object sender, RoutedEventArgs e)
    {
        if (_owner is null)
        {
            return;
        }

        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");
        _ = PickFolderAsync(picker);
    }

    private async System.Threading.Tasks.Task PickFolderAsync(FolderPicker picker)
    {
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DownloadDirBox.Text = folder.Path;
        }
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        _owner?.Downloader.ClearThumbnailCache();
        StatusText.Text = "已清除缩略图缓存";
    }

    private async void OnProbeConnectivity(object sender, RoutedEventArgs e)
    {
        if (_owner is null)
        {
            return;
        }

        ProbeButton.IsEnabled = false;
        ProbeRing.IsActive = true;
        ProbeResults.Items.Clear();
        StatusText.Text = "正在检测…";
        try
        {
            var directSettings = new AppSettings
            {
                UseNoProxy = true,
                RequestTimeoutSeconds = 8,
            };
            var proxiedSettings = new AppSettings
            {
                UseNoProxy = _settings?.UseNoProxy ?? false,
                ProxyUrl = _settings?.ProxyUrl,
                RequestTimeoutSeconds = 8,
            };

            using var directHttp = new HttpClientFactory(directSettings).CreateClient();
            using var proxiedHttp = new HttpClientFactory(proxiedSettings).CreateClient();
            var results = await ConnectivityProbe.ProbeAsync(_owner.Sources, directHttp, proxiedHttp);

            foreach (var result in results)
            {
                ProbeResults.Items.Add(result switch
                {
                    { NeedsProxy: true } => new ProbeRow(
                        "\uE72E", SystemBrush("SystemFillColorCautionBrush"),
                        result.DisplayName, "需代理（直连不可达，代理可达）"),
                    { Unreachable: true } => new ProbeRow(
                        "\uE783", SystemBrush("SystemFillColorCriticalBrush"),
                        result.DisplayName, $"不可达（直连 {result.DirectDetail}；代理 {result.ProxyDetail}）"),
                    _ => new ProbeRow(
                        "\uE73E", SystemBrush("SystemFillColorSuccessBrush"),
                        result.DisplayName, $"直连可用（{result.DirectDetail}）"),
                });
            }

            StatusText.Text = $"检测完成，共 {results.Count} 个源";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检测失败：{ex.Message}";
        }
        finally
        {
            ProbeButton.IsEnabled = true;
            ProbeRing.IsActive = false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _store is null || _owner is null)
        {
            return;
        }

        _settings.NsfwMode = NsfwSegmented.SelectedIndex switch
        {
            1 => NsfwModeStorage.OnlyNsfw,
            2 => NsfwModeStorage.ShowEverything,
            _ => NsfwModeStorage.BlockNsfw,
        };
        _settings.Theme = ThemeSegmented.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "default",
        };
        _settings.AutoReloadIntervalSeconds = (int)IntervalBox.Value;
        _settings.GalleryCount = (int)CountBox.Value;
        _settings.AutoReloadEnabled = AutoReloadCheck.IsOn;
        _settings.ProxyUrl = string.IsNullOrWhiteSpace(ProxyBox.Text) ? null : ProxyBox.Text.Trim();
        _settings.UseNoProxy = NoProxyCheck.IsChecked == true;
        _settings.RequestTimeoutSeconds = (int)TimeoutBox.Value;
        _settings.GelbooruUserId = string.IsNullOrWhiteSpace(GelbooruUserIdBox.Text)
            ? null
            : GelbooruUserIdBox.Text.Trim();
        _settings.GelbooruApiKey = string.IsNullOrWhiteSpace(GelbooruApiKeyBox.Text)
            ? null
            : GelbooruApiKeyBox.Text.Trim();
        _settings.DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirBox.Text)
            ? null
            : DownloadDirBox.Text.Trim();
        _settings.MaxConcurrentDownloads = (int)ConcurrencyBox.Value;
        _settings.EnableThumbnailCache = CacheToggle.IsOn;
        _settings.ThumbnailProxyTemplate = string.IsNullOrWhiteSpace(ProxyTemplateBox.Text)
            ? null
            : ProxyTemplateBox.Text.Trim();

        var forbidden = false;
        foreach (var (sourceId, box) in _tagBoxes)
        {
            _settings.SourceTags[sourceId] = box.Text.Trim();
            if (sourceId == "danbooru" && DanbooruSource.ContainsForbiddenTag(box.Text))
            {
                forbidden = true;
            }
        }

        _store.Save(_settings);
        _owner.ApplySettings();
        _settings = _owner.Settings;
        WarningBar.Title = "标签包含受限词（loli / shota），已自动过滤";
        WarningBar.Message = "这些标签不会被发送到 Danbooru，其他标签保留。";
        WarningBar.IsOpen = forbidden;
        StatusText.Text = "设置已保存";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_store is null || _owner is null)
        {
            return;
        }

        var fresh = new AppSettings();
        _store.Save(fresh);
        _settings = fresh;
        _owner.ApplySettings();
        _settings = _owner.Settings;
        ReloadControls();
        WarningBar.IsOpen = false;
        StatusText.Text = "已恢复默认设置";
    }

    private void ReloadControls()
    {
        if (_settings is null || _store is null)
        {
            return;
        }

        NsfwSegmented.SelectedIndex = _settings.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => 1,
            NsfwModeStorage.ShowEverything => 2,
            _ => 0,
        };
        ThemeSegmented.SelectedIndex = _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        IntervalBox.Value = _settings.AutoReloadIntervalSeconds;
        CountBox.Value = _settings.GalleryCount;
        AutoReloadCheck.IsOn = _settings.AutoReloadEnabled;
        ProxyBox.Text = _settings.ProxyUrl ?? string.Empty;
        NoProxyCheck.IsChecked = _settings.UseNoProxy;
        TimeoutBox.Value = _settings.RequestTimeoutSeconds;
        GelbooruUserIdBox.Text = _settings.GelbooruUserId ?? string.Empty;
        GelbooruApiKeyBox.Text = _settings.GelbooruApiKey ?? string.Empty;
        DownloadDirBox.Text = _settings.DownloadDirectory ?? string.Empty;
        ConcurrencyBox.Value = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 16);
        CacheToggle.IsOn = _settings.EnableThumbnailCache;
        ProxyTemplateBox.Text = _settings.ThumbnailProxyTemplate ?? string.Empty;
        foreach (var (sourceId, box) in _tagBoxes)
        {
            box.Text = SettingsStore.GetSourceTags(_settings, sourceId);
        }
    }
}
