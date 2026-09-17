using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AnimeDownloader.App;

/// <summary>
/// 让应用完全适配用户自己的 Windows 个性化配色（强调色 + 亮暗模式）。
/// XAML 层的 AppAccent* 笔刷通过 ThemeResource 引用框架的 SystemAccentColor*；
/// 本服务在运行中监听 <see cref="UISettings.ColorValuesChanged"/>，把系统最新强调色
/// 直接写入应用级 SystemAccentColor* 覆盖并就地改写笔刷实例，实现运行中实时跟随。
/// 注意：刻意不使用 "切换 RequestedTheme 强制主题重求值" 的技巧——
/// 在动画/Mica 进行中同步双切换会在 XAML 原生层触发 0xc0000005 访问冲突；
/// 原生控件的强调色将在下次主题切换或重启时自然刷新。
/// </summary>
internal sealed class WindowsColorScheme
{
    public static WindowsColorScheme Instance { get; } = new();

    /// <summary>强调色实心块上的文字亮度阈值：高于它用近黑字，否则用白字。</summary>
    private const double InkLuminanceThreshold = 0.4;

    /// <summary>Dark 主题下强调色底衬的不透明度。</summary>
    private const double DarkSoftOpacity = 0.16;

    /// <summary>Light 主题下强调色底衬的不透明度（浅底上需要更克制）。</summary>
    private const double LightSoftOpacity = 0.12;

    /// <summary>Dark 主题下强调色描边的不透明度。</summary>
    private const double DarkLineOpacity = 0.42;

    /// <summary>Light 主题下强调色描边的不透明度。</summary>
    private const double LightLineOpacity = 0.45;

    private readonly List<WeakReference<FrameworkElement>> _roots = [];
    private UISettings? _uiSettings;
    private DispatcherQueue? _dispatcherQueue;
    private bool _subscribed;
    private bool _applying;
    private (Color Accent, Color Light1, Color Light2, Color Light3, Color Dark1, Color Dark2, Color Dark3)? _lastApplied;

    private WindowsColorScheme()
    {
    }

    /// <summary>注册随配色变化需要刷新的主题根（主窗口与查看器窗口的 Root）。须在 UI 线程调用。</summary>
    public void Register(FrameworkElement themeRoot)
    {
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        _uiSettings ??= new UISettings();
        if (!_subscribed)
        {
            // ColorValuesChanged 在非 UI 线程触发，经 DispatcherQueue 切回 UI 线程再应用。
            _uiSettings.ColorValuesChanged += (_, _) => _dispatcherQueue?.TryEnqueue(Apply);
            _subscribed = true;
        }

        _roots.RemoveAll(weak => !weak.TryGetTarget(out _));
        _roots.Add(new WeakReference<FrameworkElement>(themeRoot));

        // 注册即应用一次：XAML 里引用的 SystemAccentColor* 在强调色变更后不一定
        // 会被框架即时重求值，而本窗口是在启动后才注册的 —— 不主动写一次，
        // 首帧可能停在框架的旧值上（或者在有本地覆盖时停在 XAML 的默认值上）。
        Apply();
    }

    /// <summary>读取系统当前配色并应用到全部已注册的主题根。</summary>
    public void Apply()
    {
        if (_uiSettings is null || _applying)
        {
            return;
        }

        _applying = true;
        try
        {
            Color accent, light1, light2, light3, dark1, dark2, dark3;
            try
            {
                accent = _uiSettings.GetColorValue(UIColorType.Accent);
                light1 = _uiSettings.GetColorValue(UIColorType.AccentLight1);
                light2 = _uiSettings.GetColorValue(UIColorType.AccentLight2);
                light3 = _uiSettings.GetColorValue(UIColorType.AccentLight3);
                dark1 = _uiSettings.GetColorValue(UIColorType.AccentDark1);
                dark2 = _uiSettings.GetColorValue(UIColorType.AccentDark2);
                dark3 = _uiSettings.GetColorValue(UIColorType.AccentDark3);
            }
            catch (Exception)
            {
                // 取色失败（理论上仅在异常环境出现）时保留当前配色。
                return;
            }

            var snapshot = (accent, light1, light2, light3, dark1, dark2, dark3);
            if (_lastApplied == snapshot)
            {
                return;
            }

            _lastApplied = snapshot;
            var resources = Application.Current.Resources;

            // 1) 应用级 SystemAccentColor* 覆盖：保证此后的 ThemeResource 求值取到系统最新值
            //    （WinUI 3 框架内建的 SystemAccentColor 在强调色变更后可能不即时刷新）。
            resources["SystemAccentColor"] = accent;
            resources["SystemAccentColorLight1"] = light1;
            resources["SystemAccentColorLight2"] = light2;
            resources["SystemAccentColorLight3"] = light3;
            resources["SystemAccentColorDark1"] = dark1;
            resources["SystemAccentColorDark2"] = dark2;
            resources["SystemAccentColorDark3"] = dark3;

            // 2) 就地改写主题字典中的笔刷实例：应用自有界面即时变色，
            //    与普通画刷属性动画同路径，不触发框架主题重走（安全）。
            //    Dark：主强调 = 系统原色，悬停取 Light1（更亮）。
            //    Light：主强调取 Dark1（白底上要更深才站得住），悬停再深一档。
            ApplyToThemeDictionary(
                resources,
                "Dark",
                accent: accent,
                accentStrong: light1,
                lineColor: accent,
                softColor: accent,
                softOpacity: DarkSoftOpacity,
                lineOpacity: DarkLineOpacity);
            ApplyToThemeDictionary(
                resources,
                "Light",
                accent: dark1,
                accentStrong: dark2,
                lineColor: dark1,
                softColor: accent,
                softOpacity: LightSoftOpacity,
                lineOpacity: LightLineOpacity);
        }
        catch (Exception ex)
        {
            TryLog($"WindowsColorScheme.Apply failed: {ex}");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// 把一套强调色写进某个主题字典里的全部 AppAccent* 笔刷。
    /// 之所以逐个就地改写而不是让 XAML 靠 ThemeResource 自己重求值：WinUI 3 内建的
    /// SystemAccentColor 在强调色变更后不保证即时刷新，而重设 RequestedTheme 强制
    /// 重求值会在 XAML 原生层崩溃（见类型注释）。
    /// </summary>
    private static void ApplyToThemeDictionary(
        ResourceDictionary resources,
        string themeKey,
        Color accent,
        Color accentStrong,
        Color lineColor,
        Color softColor,
        double softOpacity,
        double lineOpacity)
    {
        if (!resources.ThemeDictionaries.TryGetValue(themeKey, out var value) || value is not ResourceDictionary theme)
        {
            return;
        }

        SetBrush(theme, "AppAccentBrush", accent);
        SetBrush(theme, "AppAccentStrongBrush", accentStrong);
        SetBrush(theme, "AppFocusRingBrush", accent);
        SetBrush(theme, "AppStatusBusyBrush", accent);
        SetBrush(theme, "AppAccentSoftBrush", softColor, softOpacity);
        SetBrush(theme, "AppAccentLineBrush", lineColor, lineOpacity);
        // 压在实心强调块上的文字：按强调色自身的亮度取黑或白。
        // 写死白色在 Windows 的浅色强调色（黄 / 薄荷 / 浅粉）上会直接糊掉。
        SetBrush(theme, "AppAccentInkBrush", InkFor(accent));
    }

    /// <summary>
    /// 依据强调色的相对亮度挑选它上面的文字色。
    /// 用 WCAG 的线性化公式而不是裸通道均值：绿色权重高，肉眼感受才对得上。
    /// </summary>
    private static Color InkFor(Color accent)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Linearize(accent.R))
            + (0.7152 * Linearize(accent.G))
            + (0.0722 * Linearize(accent.B));

        return luminance > InkLuminanceThreshold
            ? Color.FromArgb(0xFF, 0x14, 0x15, 0x18)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private static void SetBrush(ResourceDictionary theme, string key, Color color, double? opacity = null)
    {
        if (theme.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
            if (opacity is { } target)
            {
                brush.Opacity = target;
            }
        }
    }

    private static void TryLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {message}\n");
        }
        catch
        {
            // 日志写入失败不阻止运行。
        }
    }
}
