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
            //    Dark：主强调 = 系统原色；Light：主强调用 Dark1 变体保证浅色表面对比度。
            ApplyToThemeDictionary(resources, "Dark", softColor: accent, strongAccent: accent);
            ApplyToThemeDictionary(resources, "Light", softColor: accent, strongAccent: dark1);
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

    private static void ApplyToThemeDictionary(
        ResourceDictionary resources,
        string themeKey,
        Color softColor,
        Color strongAccent)
    {
        const double darkSoftOpacity = 0.15;
        const double lightSoftOpacity = 0.09;
        if (!resources.ThemeDictionaries.TryGetValue(themeKey, out var value) || value is not ResourceDictionary theme)
        {
            return;
        }

        SetBrushColor(theme, "AppAccentBrush", strongAccent);
        SetBrushColor(theme, "AppAccentBrush2", strongAccent);
        SetBrushColor(theme, "AppAccentGradientBrush", strongAccent);
        if (theme.TryGetValue("AppAccentSoftBrush", out var softValue) && softValue is SolidColorBrush soft)
        {
            soft.Color = softColor;
            soft.Opacity = themeKey == "Dark" ? darkSoftOpacity : lightSoftOpacity;
        }
    }

    private static void SetBrushColor(ResourceDictionary theme, string key, Color color)
    {
        if (theme.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
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
