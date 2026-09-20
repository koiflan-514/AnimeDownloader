using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AnimeDownloader.App.Views;

/// <summary>探测结果行：状态用 Fluent 图标 + 语义色（模板绑定用，因此是顶层公开类型）。</summary>
/// <param name="StatusGlyph">状态字形（Segoe Fluent Icons 码位）。</param>
/// <param name="StatusBrush">状态颜色。</param>
/// <param name="DisplayName">图源显示名。</param>
/// <param name="StatusText">一句话结论。</param>
public sealed record ProbeRow(string StatusGlyph, IBrush? StatusBrush, string DisplayName, string StatusText);

/// <summary>
/// 设置页：NSFW 过滤、主题、刷新间隔、画廊数量、代理 / 超时、下载目录、并发数、
/// 缩略图缓存与各图源标签。保存后通过 <see cref="MainWindow.ApplySettings"/> 立即生效。
/// </summary>
public sealed partial class SettingsPage : UserControl, IModePage
{
    private MainWindow? _owner;
    private SettingsStore? _store;
    private AppSettings? _settings;
    private IReadOnlyList<IImageSource> _sources = Array.Empty<IImageSource>();
    private readonly Dictionary<string, TextBox> _tagBoxes = new();

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

        ReloadControls();
        WarningBar.IsOpen = false;

        TagsPanel.Children.Clear();
        _tagBoxes.Clear();
        foreach (var source in _sources.Where(s => s.SupportsTags))
        {
            var label = new TextBlock
            {
                Text = $"{source.DisplayName} 标签",
                Theme = Controls.AppTheme.Lookup("AppBodyStrong"),
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

    private void OnBrowseDir(object? sender, RoutedEventArgs e)
    {
        if (_owner is null)
        {
            return;
        }

        _ = PickFolderAsync();
    }

    private async Task PickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择图片保存目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
        {
            DownloadDirBox.Text = folders[0].Path.LocalPath;
        }
    }

    private void OnClearCache(object? sender, RoutedEventArgs e)
    {
        _owner?.Downloader.ClearThumbnailCache();
        StatusText.Text = "已清除缩略图缓存";
    }

    private async void OnProbeConnectivity(object? sender, RoutedEventArgs e)
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

            // 三档语义色沿用暗房的状态语汇，而不是 WinUI 的 SystemFillColor*：
            //     直连可用 → 安静灰（无事发生）
            //     需代理   → **强调色**（需要你注意；本设计不引入第二个暖色）
            //     不可达   → 状态红（故障）
            foreach (var result in results)
            {
                ProbeResults.Items.Add(result switch
                {
                    { NeedsProxy: true } => new ProbeRow(
                        "\uE72E", Res("AppAccentBrush"),
                        result.DisplayName, "需代理（直连不可达，代理可达）"),
                    { Unreachable: true } => new ProbeRow(
                        "\uE783", Res("AppStatusErrorBrush"),
                        result.DisplayName, $"不可达（直连 {result.DirectDetail}；代理 {result.ProxyDetail}）"),
                    _ => new ProbeRow(
                        "\uE73E", Res("AppStatusOkBrush"),
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

    /// <summary>按 key 取一支画笔；取不到时返回 null（图标退回继承的前景色，而不是崩掉）。</summary>
    /// <remarks>
    /// Avalonia 12 没有 <c>TryFindResource(key, out value)</c>，对应物是
    /// <see cref="StyledElement.TryGetResource(object, ThemeVariant, out object?)"/>，
    /// 必须显式传主题变体（这里就是本控件当前的 <c>ActualThemeVariant</c>）。
    /// </remarks>
    private IBrush? Res(string key) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    private void OnSave(object? sender, RoutedEventArgs e)
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
        _settings.AutoReloadIntervalSeconds = ToInt(IntervalBox.Value, 30);
        _settings.GalleryCount = ToInt(CountBox.Value, 12);
        _settings.AutoReloadEnabled = AutoReloadCheck.IsChecked == true;
        _settings.ProxyUrl = string.IsNullOrWhiteSpace(ProxyBox.Text) ? null : ProxyBox.Text.Trim();
        _settings.UseNoProxy = NoProxyCheck.IsChecked == true;
        _settings.RequestTimeoutSeconds = ToInt(TimeoutBox.Value, 20);
        _settings.GelbooruUserId = string.IsNullOrWhiteSpace(GelbooruUserIdBox.Text)
            ? null
            : GelbooruUserIdBox.Text.Trim();
        _settings.GelbooruApiKey = string.IsNullOrWhiteSpace(GelbooruApiKeyBox.Text)
            ? null
            : GelbooruApiKeyBox.Text.Trim();
        _settings.DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirBox.Text)
            ? null
            : DownloadDirBox.Text.Trim();
        _settings.MaxConcurrentDownloads = ToInt(ConcurrencyBox.Value, 4);
        _settings.EnableThumbnailCache = CacheToggle.IsChecked == true;
        _settings.ThumbnailProxyTemplate = string.IsNullOrWhiteSpace(ProxyTemplateBox.Text)
            ? null
            : ProxyTemplateBox.Text.Trim();

        var forbidden = false;
        foreach (var (sourceId, box) in _tagBoxes)
        {
            _settings.SourceTags[sourceId] = (box.Text ?? string.Empty).Trim();
            if (sourceId == "danbooru" && DanbooruSource.ContainsForbiddenTag(box.Text ?? string.Empty))
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

    private void OnReset(object? sender, RoutedEventArgs e)
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

    /// <summary>NumericUpDown 的 Value 是 decimal?，空值时用下限兜底，避免 (int)null 抛异常。</summary>
    private static int ToInt(decimal? value, int fallback) =>
        value is { } v ? (int)v : fallback;

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
        AutoReloadCheck.IsChecked = _settings.AutoReloadEnabled;
        ProxyBox.Text = _settings.ProxyUrl ?? string.Empty;
        NoProxyCheck.IsChecked = _settings.UseNoProxy;
        TimeoutBox.Value = _settings.RequestTimeoutSeconds;
        GelbooruUserIdBox.Text = _settings.GelbooruUserId ?? string.Empty;
        GelbooruApiKeyBox.Text = _settings.GelbooruApiKey ?? string.Empty;
        DownloadDirBox.Text = _settings.DownloadDirectory ?? string.Empty;
        ConcurrencyBox.Value = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 16);
        CacheToggle.IsChecked = _settings.EnableThumbnailCache;
        ProxyTemplateBox.Text = _settings.ThumbnailProxyTemplate ?? string.Empty;
        foreach (var (sourceId, box) in _tagBoxes)
        {
            box.Text = SettingsStore.GetSourceTags(_settings, sourceId);
        }
    }
}
