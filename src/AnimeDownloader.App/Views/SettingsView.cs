using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AnimeDownloader.App.Controls;
using AnimeDownloader.App.Design;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Download.Contracts;
using AnimeDownloader.Core.Sources;
using System.Globalization;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 设置页 —— 移动端重设计版。
/// </summary>
/// <remarks>
/// <para>版式按设计稿：顶部「返回 + 设置」页眉，下面是一列<b>分组卡片</b>（圆角 12、
/// bg-surface 底、组间 24dp），组内是 56dp 高的设置行。设置行只有两种形态：</para>
/// <list type="bullet">
///   <item><description><b>开关行</b>：左侧标题 + 副标题，右侧 48×28 开关。</description></item>
///   <item><description><b>键值行</b>：标题在上、当前值在下，整体居中，整行可点。</description></item>
/// </list>
///
/// <para>设计稿里<b>不</b>存在分段选择器 —— 所以旧版的 SegmentedControl 全部退场：
/// 多选项改由键值行弹出选择对话框承载，布尔项直接落到开关上。每一行的点击目标因此
/// 稳定在 56dp，比原来把控件塞进行里更好按。</para>
///
/// <para><b>改动即保存</b>：设计稿没有「保存」按钮，所以每个控件变化都立即写回
/// <see cref="AppServices.SaveSettings"/>；主题变化会通知外壳重建界面。</para>
/// </remarks>
internal sealed class SettingsView : ScrollView
{
    private readonly Dk _dk;
    private readonly AppServices _services;
    private readonly LinearLayout _content;
    private string _initialTheme = "default";

    /// <summary>底部悬浮 Tab Bar 的高度（px）；内容整体让出这块空间，最后一组才不会被它压住。</summary>
    private int _bottomBarHeightPx;

    // 设计稿的三组
    private readonly SwitchView _darkSwitch;
    private readonly TextView _darkSubtitle;
    private readonly SettingsValueRowView _sourceRow;
    private readonly SettingsValueRowView _nsfwRow;
    private readonly SettingsValueRowView _blockedTagsRow;
    private readonly SwitchView _randomSwitch;

    private readonly SettingsValueRowView _autoReloadRow;
    private readonly SettingsValueRowView _galleryCountRow;
    private readonly SettingsValueRowView _downloadPathRow;
    private readonly SettingsValueRowView _maxConcurrentRow;
    private readonly SwitchView _wifiOnlySwitch;
    private readonly SettingsValueRowView _saveLocationRow;
    private readonly SwitchView _notifySwitch;

    // 设计稿之外的补充组（保住原有功能）
    private readonly SettingsValueRowView _proxyRow;
    private readonly SwitchView _noProxySwitch;
    private readonly SettingsValueRowView _timeoutRow;
    private readonly SettingsValueRowView _gelbooruUserRow;
    private readonly SettingsValueRowView _gelbooruKeyRow;
    private readonly SwitchView _thumbnailCacheSwitch;
    private readonly SettingsValueRowView _cacheRow;
    private readonly SettingsValueRowView _runtimeRow;

    internal event EventHandler? BackRequested;
    internal event EventHandler<bool>? Saved;

    internal SettingsView(Context context, Dk dk, AppServices services)
        : base(context)
    {
        _dk = dk;
        _services = services;
        SetBackgroundColor(_dk.Background);

        var content = new LinearLayout(context) { Orientation = Orientation.Vertical };
        content.SetBackgroundColor(_dk.Background);
        content.SetPadding(dk.Dpi(16f), 0, dk.Dpi(16f), dk.Dpi(16f));
        AddView(content);
        _content = content;

        BuildHeader(context, content);

        // ---------------- 组 1：浏览 ----------------
        var browse = Group(context);

        _darkSubtitle = new TextView(context);
        _darkSwitch = new SwitchView(context, dk);
        _darkSwitch.Toggled += (_, _) => OnDarkToggled();
        browse.AddView(SwitchRow(context, "深色模式", _darkSubtitle, _darkSwitch));
        // 主题实际是三态（跟随系统 / 亮色 / 暗色），而开关只有两态 ——
        // 点副标题轮换出「亮色」这一档，既保住设计稿的开关形态又不丢选项。
        _darkSubtitle.Clickable = true;
        _darkSubtitle.Click += (_, _) => CycleTheme();

        _sourceRow = new SettingsValueRowView(context, dk, "默认源", "—");
        _sourceRow.Click += (_, _) => PickSource();
        browse.AddView(_sourceRow);

        _nsfwRow = new SettingsValueRowView(context, dk, "NSFW 过滤", "—");
        _nsfwRow.Click += (_, _) => PickNsfwMode();
        browse.AddView(_nsfwRow);

        _blockedTagsRow = new SettingsValueRowView(context, dk, "屏蔽标签", "—");
        _blockedTagsRow.Click += (_, _) => EditBlockedTags();
        browse.AddView(_blockedTagsRow);

        _randomSwitch = new SwitchView(context, dk);
        _randomSwitch.Toggled += (_, _) => OnRandomToggled();
        browse.AddView(SwitchRow(context, "随机浏览", null, _randomSwitch, "开启后每次下拉刷新随机加载"));

        content.AddView(browse, GroupParams());

        // ---------------- 组 2：下载 ----------------
        var download = Group(context);

        _autoReloadRow = new SettingsValueRowView(context, dk, "自动刷新间隔", "—");
        _autoReloadRow.Click += (_, _) => PromptNumber(
            "自动刷新间隔（秒）", 1, 9999, Math.Clamp(_services.Settings.AutoReloadIntervalSeconds, 1, 9999), value =>
            {
                _services.Settings.AutoReloadIntervalSeconds = value;
                Commit();
            });
        download.AddView(_autoReloadRow);

        _galleryCountRow = new SettingsValueRowView(context, dk, "每批图片数", "—");
        _galleryCountRow.Click += (_, _) => PromptNumber(
            "每批图片数（1–48）", 1, 48, Math.Clamp(_services.Settings.GalleryCount > 0 ? _services.Settings.GalleryCount : 12, 1, 48), value =>
            {
                _services.Settings.GalleryCount = value;
                Commit();
            });
        download.AddView(_galleryCountRow);

        _downloadPathRow = new SettingsValueRowView(context, dk, "下载路径", "—");
        _downloadPathRow.Click += (_, _) => PromptText(
            "下载路径（留空回落默认）", _services.Prefs.AlbumName ?? string.Empty, value =>
            {
                _services.Prefs.AlbumName = string.IsNullOrWhiteSpace(value) ? "AnimeDownloader" : value.Trim();
                Commit();
            });
        download.AddView(_downloadPathRow);

        _maxConcurrentRow = new SettingsValueRowView(context, dk, "同时下载数", "—");
        _maxConcurrentRow.Click += (_, _) => PromptNumber(
            "同时下载数（1–8）", 1, 8, Math.Clamp(_services.Settings.MaxConcurrentDownloads, 1, 8), value =>
            {
                _services.Settings.MaxConcurrentDownloads = value;
                Commit();
            });
        download.AddView(_maxConcurrentRow);

        _wifiOnlySwitch = new SwitchView(context, dk);
        _wifiOnlySwitch.Toggled += (_, _) => OnWifiOnlyToggled();
        download.AddView(SwitchRow(context, "仅 Wi-Fi 下载", null, _wifiOnlySwitch));

        _saveLocationRow = new SettingsValueRowView(context, dk, "保存位置", "—");
        _saveLocationRow.Click += (_, _) => PickSaveLocation();
        download.AddView(_saveLocationRow);

        _notifySwitch = new SwitchView(context, dk);
        _notifySwitch.Toggled += (_, _) => OnNotifyToggled();
        download.AddView(SwitchRow(context, "下载完成通知", null, _notifySwitch));

        content.AddView(download, GroupParams());

        // ---------------- 组 3：网络与代理 ----------------
        var network = Group(context);

        _proxyRow = new SettingsValueRowView(context, dk, "手动代理地址", "—");
        _proxyRow.Click += (_, _) => PromptText(
            "手动代理地址（留空自动检测）", _services.Settings.ProxyUrl ?? string.Empty, value =>
            {
                _services.Settings.ProxyUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                Commit();
            });
        network.AddView(_proxyRow);

        _noProxySwitch = new SwitchView(context, dk);
        _noProxySwitch.Toggled += (_, _) => OnNoProxyToggled();
        network.AddView(SwitchRow(context, "忽略代理直连", null, _noProxySwitch));

        _timeoutRow = new SettingsValueRowView(context, dk, "请求超时", "—");
        _timeoutRow.Click += (_, _) => PromptNumber(
            "请求超时（5–120 秒）", 5, 120, Math.Clamp(_services.Settings.RequestTimeoutSeconds, 5, 120), value =>
            {
                _services.Settings.RequestTimeoutSeconds = value;
                Commit();
            });
        network.AddView(_timeoutRow);

        content.AddView(network, GroupParams());

        // ---------------- 组 4：Gelbooru 凭据 ----------------
        var gelbooru = Group(context);

        _gelbooruUserRow = new SettingsValueRowView(context, dk, "User ID", "—");
        _gelbooruUserRow.Click += (_, _) => PromptText(
            "Gelbooru User ID", _services.Settings.GelbooruUserId ?? string.Empty, value =>
            {
                _services.Settings.GelbooruUserId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                Commit();
            });
        gelbooru.AddView(_gelbooruUserRow);

        _gelbooruKeyRow = new SettingsValueRowView(context, dk, "API Key", "—");
        _gelbooruKeyRow.Click += (_, _) => PromptText(
            "Gelbooru API Key", _services.Settings.GelbooruApiKey ?? string.Empty, value =>
            {
                _services.Settings.GelbooruApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                Commit();
            });
        gelbooru.AddView(_gelbooruKeyRow);

        content.AddView(gelbooru, GroupParams());

        // ---------------- 组 5：缓存 ----------------
        var cache = Group(context);

        _cacheRow = new SettingsValueRowView(context, dk, "缩略图缓存", "—");
        _cacheRow.Click += (_, _) => ConfirmClearCache();
        cache.AddView(_cacheRow);

        _thumbnailCacheSwitch = new SwitchView(context, dk);
        _thumbnailCacheSwitch.Toggled += (_, _) => OnThumbnailCacheToggled();
        cache.AddView(SwitchRow(context, "启用缩略图缓存", null, _thumbnailCacheSwitch, "改动在下次启动生效"));

        content.AddView(cache, GroupParams());

        // ---------------- 组 6：关于 ----------------
        var about = Group(context);
        about.AddView(new SettingsValueRowView(context, dk, "版本", "0.4.0"));

        _runtimeRow = new SettingsValueRowView(context, dk, "运行环境", "—");
        about.AddView(_runtimeRow);

        var feedback = new SettingsValueRowView(context, dk, "反馈", "GitHub Issues");
        feedback.Click += (_, _) => OpenFeedback();
        about.AddView(feedback);

        content.AddView(about, GroupParams());

        RefreshFromServices();
    }

    /// <summary>让底部内容避开悬浮的 Tab Bar（外壳测量后回填）。</summary>
    internal void ApplyBottomBarHeight(int px)
    {
        if (px == _bottomBarHeightPx)
        {
            return;
        }

        _bottomBarHeightPx = px;
        _content.SetPadding(_dk.Dpi(16f), 0, _dk.Dpi(16f), px + _dk.Dpi(16f));
    }

    // ---------------- 数据 ↔ 界面 ----------------

    internal void RefreshFromServices()
    {
        var s = _services.Settings;
        var p = _services.Prefs;
        _initialTheme = s.Theme;

        _darkSwitch.IsOn = string.Equals(s.Theme, "dark", StringComparison.Ordinal);
        _darkSubtitle.Text = s.Theme switch
        {
            "dark" => "强制暗色",
            "light" => "亮色",
            _ => "跟随系统",
        };

        _sourceRow.ValueText = _services.CurrentSource.DisplayName;
        _nsfwRow.ValueText = s.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => "仅 NSFW",
            NsfwModeStorage.ShowEverything => "全部",
            _ => "屏蔽",
        };
        _blockedTagsRow.ValueText = $"{CountBlockedTags()} 个标签";

        _randomSwitch.IsOn = s.AutoReloadEnabled;
        _autoReloadRow.ValueText = $"{s.AutoReloadIntervalSeconds} 秒";
        _galleryCountRow.ValueText = $"{s.GalleryCount}   1–48";
        _downloadPathRow.ValueText = string.IsNullOrWhiteSpace(p.AlbumName) ? "AnimeDownloader" : p.AlbumName;
        _maxConcurrentRow.ValueText = s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        _wifiOnlySwitch.IsOn = p.WifiOnlyDownloads;
        _saveLocationRow.ValueText = p.DownloadToGallery ? "系统相册" : "应用目录";
        _notifySwitch.IsOn = p.NotifyOnCompletion;

        _proxyRow.ValueText = string.IsNullOrWhiteSpace(s.ProxyUrl) ? "自动检测" : s.ProxyUrl;
        _noProxySwitch.IsOn = s.UseNoProxy;
        _timeoutRow.ValueText = $"{s.RequestTimeoutSeconds} 秒";

        _gelbooruUserRow.ValueText = string.IsNullOrWhiteSpace(s.GelbooruUserId) ? "未设置" : s.GelbooruUserId;
        _gelbooruKeyRow.ValueText = string.IsNullOrWhiteSpace(s.GelbooruApiKey) ? "未设置" : Mask(s.GelbooruApiKey);

        _thumbnailCacheSwitch.IsOn = s.EnableThumbnailCache;
        UpdateCacheRow();

        _runtimeRow.ValueText = $"Android {Build.VERSION.Release} (API {Build.VERSION.SdkInt})";
    }

    private void UpdateCacheRow()
    {
        var (bytes, count) = _services.Thumbnails.Stats;
        _cacheRow.ValueText = $"{DownloadPolicy.FormatBytes(bytes)} · {count} 张（点击清理）";
    }

    private static string Mask(string value) =>
        value.Length <= 6 ? new string('•', value.Length) : $"{value[..3]}{new string('•', 6)}";

    /// <summary>把改动写回存储并刷新界面；主题变化时通知外壳重建。</summary>
    private void Commit()
    {
        _services.SaveSettings();
        var themeChanged = !string.Equals(_initialTheme, _services.Settings.Theme, StringComparison.Ordinal);
        if (themeChanged)
        {
            _initialTheme = _services.Settings.Theme;
        }

        RefreshFromServices();
        Saved?.Invoke(this, themeChanged);
    }

    // ---------------- 交互 ----------------

    private void OnDarkToggled()
    {
        _services.Settings.Theme = _darkSwitch.IsOn ? "dark" : "default";
        Commit();
    }

    private void CycleTheme()
    {
        _services.Settings.Theme = _services.Settings.Theme switch
        {
            "dark" => "default",
            "default" => "light",
            _ => "dark",
        };
        Commit();
    }

    private void OnRandomToggled()
    {
        _services.Settings.AutoReloadEnabled = _randomSwitch.IsOn;
        Commit();
    }

    private void OnWifiOnlyToggled()
    {
        _services.Prefs.WifiOnlyDownloads = _wifiOnlySwitch.IsOn;
        Commit();
    }

    private void OnNotifyToggled()
    {
        _services.Prefs.NotifyOnCompletion = _notifySwitch.IsOn;
        Commit();
    }

    private void OnNoProxyToggled()
    {
        _services.Settings.UseNoProxy = _noProxySwitch.IsOn;
        Commit();
    }

    private void OnThumbnailCacheToggled()
    {
        _services.Settings.EnableThumbnailCache = _thumbnailCacheSwitch.IsOn;
        Commit();
    }

    private void PickSource()
    {
        var names = _services.Sources.Select(s => s.DisplayName).ToArray();
        var current = Math.Max(0, _services.Sources
            .Select((src, i) => (src, i))
            .FirstOrDefault(x => x.src.Id == _services.CurrentSourceId).i);

        PromptChoice("默认源", names, current, index =>
        {
            if (index >= 0 && index < _services.Sources.Count)
            {
                _services.CurrentSourceId = _services.Sources[index].Id;
                Commit();
            }
        });
    }

    private void PickNsfwMode()
    {
        var options = new[] { "屏蔽", "仅 NSFW", "全部" };
        var current = _services.Settings.NsfwMode switch
        {
            NsfwModeStorage.OnlyNsfw => 1,
            NsfwModeStorage.ShowEverything => 2,
            _ => 0,
        };

        PromptChoice("NSFW 过滤", options, current, index =>
        {
            _services.Settings.NsfwMode = index switch
            {
                1 => NsfwModeStorage.OnlyNsfw,
                2 => NsfwModeStorage.ShowEverything,
                _ => NsfwModeStorage.BlockNsfw,
            };
            Commit();
        });
    }

    private void PickSaveLocation()
    {
        var options = new[] { "系统相册", "应用目录" };
        PromptChoice("保存位置", options, _services.Prefs.DownloadToGallery ? 0 : 1, index =>
        {
            _services.Prefs.DownloadToGallery = index == 0;
            Commit();
        });
    }

    /// <summary>图源默认标签编辑（旧版是页尾的一整块输入区，现在收进对话框）。</summary>
    private void EditBlockedTags()
    {
        var taggable = _services.Sources.Where(s => s.SupportsTags).ToList();
        if (taggable.Count == 0)
        {
            Toast("当前图源不支持标签");
            return;
        }

        var container = new LinearLayout(Context) { Orientation = Orientation.Vertical };
        container.SetPadding(_dk.Dpi(20f), _dk.Dpi(4f), _dk.Dpi(20f), 0);
        var boxes = new Dictionary<string, EditText>(StringComparer.Ordinal);

        foreach (var source in taggable)
        {
            var label = new TextView(Context) { Text = source.DisplayName };
            label.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
            label.SetTextColor(_dk.TextSecondary);
            container.AddView(label);

            var box = new EditText(Context)
            {
                Text = _services.Settings.SourceTags.TryGetValue(source.Id, out var tags) ? tags : string.Empty,
                Hint = "标签（空格分隔）",
            };
            box.SetSingleLine(true);
            box.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
            box.SetTextColor(_dk.TextPrimary);
            box.SetHintTextColor(_dk.TextTertiary);
            box.SetPadding(_dk.Dpi(12f), _dk.Dpi(10f), _dk.Dpi(12f), _dk.Dpi(10f));

            var background = new GradientDrawable();
            background.SetShape(ShapeType.Rectangle);
            background.SetCornerRadius(_dk.Dp(8f));
            background.SetColor(_dk.Sunken);
            box.Background = background;

            container.AddView(box, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = _dk.Dpi(4f),
                BottomMargin = _dk.Dpi(12f),
            });
            boxes[source.Id] = box;
        }

        new AlertDialog.Builder(Context)
            .SetTitle("屏蔽标签")!
            .SetView(container)!
            .SetPositiveButton("保存", (_, _) =>
            {
                foreach (var (id, box) in boxes)
                {
                    _services.Settings.SourceTags[id] = box.Text?.Trim() ?? string.Empty;
                }

                Commit();
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    private void ConfirmClearCache() =>
        new AlertDialog.Builder(Context)
            .SetTitle("清理缩略图缓存")!
            .SetMessage("会删除已缓存的缩略图，下次浏览重新下载。")!
            .SetPositiveButton("清理", (_, _) =>
            {
                _services.Thumbnails.Clear();
                UpdateCacheRow();
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();

    private void OpenFeedback()
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://github.com/koiflan-514/AnimeDownloader/issues"));
            Context!.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
        }
    }

    // ---------------- 对话框 ----------------

    private void PromptText(string title, string initial, Action<string> onOk)
    {
        var box = new EditText(Context) { Text = initial };
        box.SetSingleLine(true);
        box.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        box.SetTextColor(_dk.TextPrimary);
        box.SetPadding(_dk.Dpi(20f), _dk.Dpi(12f), _dk.Dpi(20f), _dk.Dpi(12f));

        new AlertDialog.Builder(Context)
            .SetTitle(title)!
            .SetView(box)!
            .SetPositiveButton("确定", (_, _) => onOk(box.Text ?? string.Empty))!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    private void PromptNumber(string title, int min, int max, int initial, Action<int> onOk)
    {
        var box = new EditText(Context)
        {
            Text = initial.ToString(CultureInfo.InvariantCulture),
            InputType = InputTypes.ClassNumber,
        };
        box.SetSingleLine(true);
        box.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        box.SetTextColor(_dk.TextPrimary);
        box.SetPadding(_dk.Dpi(20f), _dk.Dpi(12f), _dk.Dpi(20f), _dk.Dpi(12f));

        new AlertDialog.Builder(Context)
            .SetTitle(title)!
            .SetView(box)!
            .SetPositiveButton("确定", (_, _) =>
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    onOk(Math.Clamp(value, min, max));
                }
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    private void PromptChoice(string title, string[] options, int selected, Action<int> onOk) =>
        new AlertDialog.Builder(Context)
            .SetTitle(title)!
            .SetSingleChoiceItems(options, Math.Clamp(selected, 0, Math.Max(0, options.Length - 1)), (sender, e) =>
            {
                (sender as Dialog)?.Dismiss();
                onOk(e.Which);
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();

    private void Toast(string text) =>
        Android.Widget.Toast.MakeText(Context, text, Android.Widget.ToastLength.Short)?.Show();

    // ---------------- 构建辅助 ----------------

    private void BuildHeader(Context context, LinearLayout content)
    {
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(0, _dk.Dpi(8f), 0, _dk.Dpi(16f));
        content.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var back = new IconButtonView(context, _dk, DkIcons.Back);
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        header.AddView(back, new LinearLayout.LayoutParams(_dk.Dpi(40f), _dk.Dpi(40f)));

        var title = new TextView(context)
        {
            Text = "设置",
        };
        title.SetTextSize(Android.Util.ComplexUnitType.Sp, 28f);
        title.SetTypeface(_dk.FontDisplay, TypefaceStyle.Bold);
        title.SetTextColor(_dk.TextPrimary);
        header.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = _dk.Dpi(12f),
        });
    }

    /// <summary>一张分组卡片：圆角 12、bg-surface 底、组内行等高堆叠。</summary>
    private LinearLayout Group(Context context)
    {
        var card = new LinearLayout(context) { Orientation = Orientation.Vertical };

        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetCornerRadius(_dk.Dp(12f));
        background.SetColor(_dk.CardFill);
        card.Background = background;
        return card;
    }

    private LinearLayout.LayoutParams GroupParams() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.WrapContent)
    {
        TopMargin = _dk.Dpi(24f),
    };

    /// <summary>
    /// 开关行：左「标题 + 可选副标题」，右 48×28 开关；整行 56dp、左右 16dp 内边距。
    /// </summary>
    private LinearLayout SwitchRow(Context context, string title, TextView? subtitle, SwitchView toggle, string? subtitleText = null)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(_dk.Dpi(56f));
        row.SetPadding(_dk.Dpi(16f), _dk.Dpi(8f), _dk.Dpi(16f), _dk.Dpi(8f));

        var left = new LinearLayout(context) { Orientation = Orientation.Vertical };
        left.SetGravity(GravityFlags.CenterVertical);
        row.AddView(left, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var titleView = new TextView(context) { Text = title };
        titleView.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        titleView.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
        titleView.SetTextColor(_dk.TextPrimary);
        left.AddView(titleView);

        var text = subtitleText;
        if (subtitle is not null)
        {
            subtitle.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
            subtitle.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
            subtitle.SetTextColor(_dk.TextSecondary);
            left.AddView(subtitle);
        }

        if (!string.IsNullOrEmpty(text) && subtitle is null)
        {
            var caption = new TextView(context) { Text = text };
            caption.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
            caption.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
            caption.SetTextColor(_dk.TextSecondary);
            left.AddView(caption);
        }

        row.AddView(toggle, new LinearLayout.LayoutParams(_dk.Dpi(48f), _dk.Dpi(28f)));
        return row;
    }

    private int CountBlockedTags()
    {
        var count = 0;
        foreach (var (_, tags) in _services.Settings.SourceTags)
        {
            if (!string.IsNullOrWhiteSpace(tags))
            {
                count++;
            }
        }

        return count;
    }
}
