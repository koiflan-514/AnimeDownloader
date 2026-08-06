using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 设置页：NSFW 过滤、主题、自动刷新间隔、画廊数量、代理与各图源标签。
/// 保存后调用主窗口的 ApplySettings 让变更立即生效。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private MainWindow? _owner;
    private SettingsStore? _store;
    private AppSettings? _settings;
    private IReadOnlyList<IImageSource> _sources = Array.Empty<IImageSource>();
    private readonly Dictionary<string, TextBox> _tagBoxes = new();

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainWindow owner)
        {
            return;
        }

        _owner = owner;
        _store = owner.SettingsStore;
        _settings = _store.Load();
        _sources = owner.Sources;

        // 通用
        NsfwCombo.SelectedIndex = _settings.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => 1,
            NsfwModeStorage.ShowEverything => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        IntervalBox.Value = _settings.AutoReloadIntervalSeconds;
        CountBox.Value = _settings.GalleryCount;
        AutoReloadCheck.IsChecked = _settings.AutoReloadEnabled;

        // 代理
        ProxyBox.Text = _settings.ProxyUrl ?? string.Empty;
        NoProxyCheck.IsChecked = _settings.UseNoProxy;

        // 图源标签
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _store is null || _owner is null)
        {
            return;
        }

        _settings.NsfwMode = NsfwCombo.SelectedIndex switch
        {
            1 => NsfwModeStorage.OnlyNsfw,
            2 => NsfwModeStorage.ShowEverything,
            _ => NsfwModeStorage.BlockNsfw,
        };
        _settings.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";
        _settings.AutoReloadIntervalSeconds = (int)IntervalBox.Value;
        _settings.GalleryCount = (int)CountBox.Value;
        _settings.AutoReloadEnabled = AutoReloadCheck.IsChecked == true;
        _settings.ProxyUrl = string.IsNullOrWhiteSpace(ProxyBox.Text) ? null : ProxyBox.Text.Trim();
        _settings.UseNoProxy = NoProxyCheck.IsChecked == true;

        foreach (var (sourceId, box) in _tagBoxes)
        {
            _settings.SourceTags[sourceId] = box.Text.Trim();
        }

        _store.Save(_settings);
        _owner.ApplySettings();
        StatusText.Text = "设置已保存";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_store is null || _owner is null)
        {
            return;
        }

        // 直接覆盖为默认并重载页面状态
        var fresh = new AppSettings();
        _store.Save(fresh);
        _settings = fresh;
        _owner.ApplySettings();
        ReloadControls();
        StatusText.Text = "已恢复默认设置";
    }

    /// <summary>将控件值重置为当前 _settings 的内容。</summary>
    private void ReloadControls()
    {
        if (_settings is null || _store is null)
        {
            return;
        }

        NsfwCombo.SelectedIndex = _settings.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => 1,
            NsfwModeStorage.ShowEverything => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = _settings.Theme switch
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
        foreach (var (sourceId, box) in _tagBoxes)
        {
            box.Text = SettingsStore.GetSourceTags(_settings, sourceId);
        }
    }
}
