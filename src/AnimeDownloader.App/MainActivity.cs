using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using AnimeDownloader.Download;
using AnimeDownloader.Download.Contracts;
using AnimeDownloader.App.Controls;
using AnimeDownloader.App.Design;
using AnimeDownloader.App.Platform;
using AnimeDownloader.App.Views;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.App;

/// <summary>
/// 唯一的 Activity：整个应用的外壳。
/// </summary>
/// <remarks>
/// <para><b>外壳只做四件事</b>：装配三个页面、提供底部胶囊导航、处理边到边内边距、把系统返回键的顺序理清。
/// 它<b>不</b>承载业务状态 —— 图源、当前页、下载进度都在各自的页面或 <see cref="AppServices"/> 里。</para>
///
/// <para>新设计采用底部胶囊 Tab Bar，固定 3 个一级入口：画廊、下载、设置。
/// 图片查看页为全屏浮层，通过返回按钮或边缘手势退出，不是 Tab 的一项。</para>
///
/// <para><b>配置变化不重建</b>：<c>ConfigurationChanges</c> 声明了方向、尺寸、字号等，
/// 于是旋转屏幕时系统不会销毁重建 Activity —— 图片、滚动位置、正在跑的下载全都保得住。
/// 代价是必须自己响应 <see cref="OnConfigurationChanged"/> 重排界面。</para>
/// </remarks>
[Activity(
    Label = "@string/app_name",
    Icon = "@mipmap/ic_launcher",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        | ConfigChanges.UiMode
        | ConfigChanges.FontScale
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.LayoutDirection,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : Activity
{
    private const int NotificationPermissionRequest = 0x9001;
    private const int TabGallery = 0;
    private const int TabDownloads = 1;
    private const int TabSettings = 2;

    private AppServices _services = null!;
    private Dk _dk = null!;
    private ScreenProfile _profile;

    private FrameLayout _root = null!;
    private FrameLayout _contentArea = null!;
    private FrameLayout _tabBarContainer = null!;
    private LinearLayout _tabBarPill = null!;
    private readonly List<BottomTabItemView> _tabs = [];

    private GalleryView _gallery = null!;
    private DownloadsView? _downloads;
    private SettingsView? _settings;
    private FrameLayout? _lightboxOverlay;
    private LightboxView? _lightbox;

    private AndroidDownloadModule _downloadModule = null!;
    private int _bottomInsetPx;
    private int _section = TabGallery;
    private int _lightboxIndex = -1;
    private IReadOnlyList<ImageItem> _lightboxItems = [];

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _services = AnimeDownloaderApplication.Services;
        _profile = ScreenProfile.For(Resources!.Configuration!);
        _dk = Dk.Create(this, _services.ThemeMode);

        _downloadModule = _services.CreateDownloadModule(typeof(MainActivity));
        _downloadModule.SnapshotChanged += OnDownloadSnapshotChanged;

        EdgeToEdge.Apply(this, FindViewById(Android.Resource.Id.Content)!, ApplyInsets);
        EdgeToEdge.ApplySystemBarContrast(this, _dk.IsLight);

        BuildShell();
        ShowSection(TabGallery);

        RequestNotificationPermissionIfNeeded();
    }

    /// <inheritdoc />
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ArgumentNullException.ThrowIfNull(newConfig);

        var profile = ScreenProfile.For(newConfig);
        var themeMode = _services.ThemeMode;
        var themeChanged = Dk.Create(this, themeMode).IsLight != _dk.IsLight;

        _profile = profile;
        _dk = Dk.Create(this, themeMode);
        EdgeToEdge.ApplySystemBarContrast(this, _dk.IsLight);
        Rebuild();

        if (themeChanged)
        {
            // 主题换了必须重建内容视图：所有控件的颜色都是构造期从令牌取的。
            ShowSection(_section);
        }
        else
        {
            ApplyMetrics();
        }
    }

    /// <inheritdoc />
    public override void OnBackPressed()
    {
        // 返回键顺序：灯箱浮层 → 回到画廊 → 退出应用。
        if (_lightbox?.HandleBackPressed() == true)
        {
            return;
        }

        if (_lightboxOverlay is { Visibility: ViewStates.Visible })
        {
            CloseLightbox();
            return;
        }

        if (_section != TabGallery)
        {
            ShowSection(TabGallery);
            return;
        }

        Finish();
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        _downloadModule.SnapshotChanged -= OnDownloadSnapshotChanged;
        base.OnDestroy();
    }

    // ---------------- 外壳装配 ----------------

    private void BuildShell()
    {
        _root = new FrameLayout(this);

        _contentArea = new FrameLayout(this);
        _tabBarContainer = new FrameLayout(this);

        _root.AddView(_contentArea, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        _root.AddView(_tabBarContainer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom,
        });

        BuildTabBar();
        SetContentView(_root);
    }

    private void BuildTabBar()
    {
        _tabBarContainer.RemoveAllViews();
        _tabs.Clear();

        var pill = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        pill.SetGravity(GravityFlags.Center);
        // 设计稿：胶囊内边距左右 24、上下 4，高 62，圆角 36，1px 描边。
        pill.SetPadding(_dk.Dpi(24f), _dk.Dpi(4f), _dk.Dpi(24f), _dk.Dpi(4f));

        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetCornerRadius(_dk.Dp(36f));
        background.SetColor(_dk.CardFill);
        background.SetStroke(_dk.Dpi(1f), _dk.Hairline);
        pill.Background = background;

        var items = new (string Icon, string Label)[]
        {
            (DkIcons.Gallery, "画廊"),
            (DkIcons.Download, "下载"),
            (DkIcons.Settings, "设置"),
        };

        for (var i = 0; i < items.Length; i++)
        {
            var index = i;
            var tab = new BottomTabItemView(this, _dk, items[i].Icon, items[i].Label);
            tab.Click += (_, _) => ShowSection(index);
            pill.AddView(tab, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
            _tabs.Add(tab);
        }

        var pillParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            _dk.Dpi(62f))
        {
            LeftMargin = _dk.Dpi(16f),
            RightMargin = _dk.Dpi(16f),
            BottomMargin = _dk.Dpi(16f),
        };
        _tabBarContainer.AddView(pill, pillParams);
        _tabBarPill = pill;
    }

    /// <summary>底部 Tab Bar 占掉的竖直空间（px）：胶囊 62 + 下边距 16 + 系统手势条安全区。</summary>
    private int BottomBarHeight => _dk.Dpi(78f) + _bottomInsetPx;

    private void Rebuild()
    {
        _root.RemoveAllViews();
        _tabBarContainer.RemoveAllViews();
        _tabs.Clear();
        BuildShell();
        ShowSection(_section);
    }

    private void RefreshTabSelection()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Selected = i == _section;
        }
    }

    // ---------------- 页面切换 ----------------

    private void ShowSection(int index)
    {
        _section = index;
        _contentArea.RemoveAllViews();
        RefreshTabSelection();

        switch (index)
        {
            case TabDownloads:
                _contentArea.AddView(EnsureDownloads(), MatchBoth());
                break;
            case TabSettings:
                _contentArea.AddView(EnsureSettings(), MatchBoth());
                break;
            default:
                _contentArea.AddView(EnsureGallery(), MatchBoth());
                break;
        }

        ApplyMetrics();
    }

    private static FrameLayout.LayoutParams MatchBoth() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);

    private GalleryView EnsureGallery()
    {
        if (_gallery is null)
        {
            _gallery = new GalleryView(this, _dk, _services);
            _gallery.OpenLightboxRequested += (_, args) => OpenLightbox(args.Items, args.Index);
            _gallery.SaveItemRequested += (_, item) => SaveItems([item]);
            _gallery.OpenSourceRequested += (_, item) => OpenExternal(item.SourceLink);
            _gallery.DownloadAllRequested += (_, _) => SaveItems(_gallery.Items);
            _gallery.SetDownloadSnapshot(_downloadModule.Current);
            // 设计稿的画廊首屏是有内容的，所以进页就拉一批，而不是等用户输入标签。
            _gallery.Reload();
        }

        return _gallery;
    }

    private DownloadsView EnsureDownloads()
    {
        if (_downloads is null)
        {
            _downloads = new DownloadsView(this, _dk, _services);
            _downloads.CancelRequested += (_, _) => _downloadModule.CancelActive();
            _downloads.SetDownloadSnapshot(_downloadModule.Current);
        }

        return _downloads;
    }

    private SettingsView EnsureSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsView(this, _dk, _services);
            _settings.BackRequested += (_, _) => ShowSection(TabGallery);
            _settings.Saved += (_, themeChanged) =>
            {
                if (themeChanged)
                {
                    Rebuild();
                }
            };
        }

        return _settings;
    }

    // ---------------- 灯箱浮层 ----------------

    private void OpenLightbox(IReadOnlyList<ImageItem> items, int index)
    {
        _lightboxItems = items;
        _lightboxIndex = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));

        _lightboxOverlay = new FrameLayout(this);
        _lightbox = new LightboxView(this, _dk, _services);
        _lightbox.CloseRequested += (_, _) => CloseLightbox();
        _lightbox.TagSelected += (_, tag) =>
        {
            CloseLightbox();
            _gallery?.SearchTag(tag);
        };
        _lightbox.SaveHandler = async item =>
        {
            var result = await _downloadModule.EnqueueAsync(new DownloadBatchRequest(
                [item],
                _services.BuildDownloadOptions()));
            if (result.Succeeded > 0)
            {
                return string.IsNullOrEmpty(result.Location) ? "已保存" : result.Location;
            }

            throw new InvalidOperationException(result.Errors.Count > 0 ? result.Errors[0] : "保存失败");
        };

        _lightboxOverlay.AddView(_lightbox, MatchBoth());
        _root.AddView(_lightboxOverlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        _lightbox.Show(_lightboxItems, _lightboxIndex);
        _tabBarContainer.Visibility = ViewStates.Gone;
    }

    private void CloseLightbox()
    {
        if (_lightboxOverlay is null)
        {
            return;
        }

        _root.RemoveView(_lightboxOverlay);
        _lightboxOverlay?.Dispose();
        _lightboxOverlay = null;
        _lightbox = null;
        _lightboxIndex = -1;
        _lightboxItems = [];
        _tabBarContainer.Visibility = ViewStates.Visible;
    }

    // ---------------- 下载 ----------------

    private void SaveItems(IReadOnlyList<ImageItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var options = _services.BuildDownloadOptions();
        _ = RunDownloadAsync(items, options);
    }

    private async Task RunDownloadAsync(IReadOnlyList<ImageItem> items, DownloadBatchOptions options)
    {
        try
        {
            var result = await _downloadModule.EnqueueAsync(new DownloadBatchRequest(items, options));
            RunOnUiThread(() =>
            {
                var text = result.Cancelled
                    ? $"已取消 · 已保存 {result.Succeeded} 张"
                    : result.Failed == 0
                        ? $"已保存 {result.Succeeded} 张（跳过 {result.Skipped}）"
                        : $"完成：成功 {result.Succeeded}，失败 {result.Failed}";
                _gallery?.ShowStatus(text, error: result.Failed > 0);
            });
        }
        catch (InvalidOperationException ex)
        {
            RunOnUiThread(() => _gallery?.ShowStatus(ex.Message, error: true));
        }
    }

    private void OnDownloadSnapshotChanged(object? sender, DownloadBatchSnapshot snapshot) =>
        RunOnUiThread(() =>
        {
            _gallery?.SetDownloadSnapshot(snapshot);
            _downloads?.SetDownloadSnapshot(snapshot);
        });

    private void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
        }
        catch (ActivityNotFoundException)
        {
            _gallery?.ShowStatus("没有可以打开这个链接的应用", error: true);
        }
    }

    // ---------------- 内边距与度量 ----------------

    private void ApplyInsets(EdgeToEdge.Insets insets)
    {
        // 内容避开系统栏；底部 Tab Bar 容器自身再加底部安全区。
        _contentArea.SetPadding(insets.Left, insets.Top, insets.Right, 0);
        _tabBarContainer.SetPadding(0, 0, 0, insets.Bottom);
        _bottomInsetPx = insets.Bottom;
        ApplyMetrics();
    }

    private void ApplyMetrics()
    {
        if (_contentArea.Width <= 0)
        {
            _contentArea.Post(ApplyMetrics);
            return;
        }

        var bottomBar = BottomBarHeight;
        _gallery?.ApplyMetrics(_contentArea.Width, _profile);
        _gallery?.ApplyBottomBarHeight(bottomBar);
        _downloads?.ApplyBottomBarHeight(bottomBar);
        _settings?.ApplyBottomBarHeight(bottomBar);
    }

    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        if (CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted)
        {
            return;
        }

        RequestPermissions([Manifest.Permission.PostNotifications], NotificationPermissionRequest);
    }
}
