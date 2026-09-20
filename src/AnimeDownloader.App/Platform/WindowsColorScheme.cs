using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Win32;

namespace AnimeDownloader.App.Platform;

/// <summary>
/// 让应用完全适配用户自己的 Windows 个性化配色（强调色 + 亮暗模式）。
///
/// 与 WinUI 版的核心差异（这也是整个迁移里最需要解释的一处）：
///     WinUI 的 <c>SystemAccentColor</c> 是**框架内建**的，并且框架同时提供
///     <c>SystemAccentColorLight1-3</c> / <c>Dark1-3</c> 六个派生色阶，
///     XAML 直接 ThemeResource 引用即可。
///     Avalonia 只提供 <c>SystemAccentColor</c> 一个键 —— 六个色阶没有对应物
///     （已用二进制检索 Avalonia.Themes.Fluent.dll 确认）。
///     所以这里必须自己把整条色阶算出来，并**就地写进** Tokens.axaml 里那组笔刷实例。
///
/// 取色优先级：
///     1. Windows 的注册表 <c>AccentPalette</c> —— 这份 32 字节的二进制正好就是
///        Windows 自己用的八个色阶（Light3 / Light2 / Light1 / Accent / Dark1 / Dark2 / Dark3 / 补色）。
///        用它意味着「Light1 和系统真正用的 Light1 是同一个值」，不是近似。
///     2. 取不到（非 Windows / 注册表被裁剪）时回退到 Avalonia 的平台配色
///        <c>IPlatformSettings.GetColorValues().AccentColor1</c>，色阶按向白/向黑线性混合派生。
///
/// 实时跟随：<c>IPlatformSettings.ColorValuesChanged</c> 在非 UI 线程触发，
/// 经 <see cref="Dispatcher.UIThread"/> 切回来再改笔刷。
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

    private const string DwmKeyPath = @"Software\Microsoft\Windows\DWM";
    private const string AccentKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private IPlatformSettings? _settings;
    private bool _subscribed;
    private bool _applying;

    /// <summary>上一次落地的色阶，用于跳过重复写入。</summary>
    private AccentRamp? _lastApplied;

    /// <summary>一条完整的强调色阶（7 档）。</summary>
    private readonly record struct AccentRamp(
        Color Accent,
        Color Light1,
        Color Light2,
        Color Light3,
        Color Dark1,
        Color Dark2,
        Color Dark3);

    private WindowsColorScheme()
    {
    }

    /// <summary>
    /// 订阅平台配色变化并立刻应用一次。须在 UI 线程调用，且要在窗口显示之前调用 ——
    /// 不然首帧会停在 Tokens.axaml 里的兜底蓝上。
    /// </summary>
    public void Start()
    {
        if (!_subscribed)
        {
            _settings = Application.Current?.PlatformSettings;
            if (_settings is not null)
            {
                _settings.ColorValuesChanged += OnColorValuesChanged;
                _subscribed = true;
            }
        }

        Apply();
    }

    /// <summary>
    /// 平台配色变化（系统强调色 / 亮暗模式被改动）。事件可能在非 UI 线程触发，
    /// 而改笔刷必须回到 UI 线程 —— 与 WinUI 版用 DispatcherQueue.TryEnqueue 同一处理。
    /// </summary>
    private void OnColorValuesChanged(object? sender, PlatformColorValues values)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Normal);
        }
    }

    /// <summary>读取系统当前配色并应用到 Tokens.axaml 里的全部强调色笔刷。</summary>
    public void Apply()
    {
        if (_applying)
        {
            return;
        }

        _applying = true;
        try
        {
            var ramp = ReadSystemRamp();
            if (ramp is null)
            {
                return;
            }

            if (_lastApplied == ramp.Value)
            {
                return;
            }

            _lastApplied = ramp.Value;
            var (accent, light1, _, _, dark1, dark2, _) = ramp.Value;

            // Dark：主强调 = 系统原色，悬停取 Light1（更亮）。
            // Light：主强调取 Dark1（白底上要更深才站得住），悬停再深一档。
            PatchTheme(
                ThemeVariant.Dark,
                accent: accent,
                accentStrong: light1,
                lineColor: accent,
                softColor: accent,
                softOpacity: DarkSoftOpacity,
                lineOpacity: DarkLineOpacity);
            PatchTheme(
                ThemeVariant.Light,
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
    /// 取系统强调色的整条色阶。
    /// </summary>
    private AccentRamp? ReadSystemRamp()
    {
        // 1) Windows 原生色阶
        if (OperatingSystem.IsWindows())
        {
            var native = ReadWindowsAccentPalette();
            if (native is { } palette)
            {
                return palette;
            }
        }

        // 2) 回退：平台接口给的强调色 + 自行派生
        var accentColor = _settings?.GetColorValues().AccentColor1 ?? default;
        return accentColor.A == 0 ? null : DeriveRamp(accentColor);
    }

    /// <summary>
    /// 读 Windows 自己用的强调色阶。
    /// <c>Explorer\Accent\AccentPalette</c> 是 32 字节 = 8 组 4 字节，
    /// 每组按 R,G,B,0 排列，顺序为 Light3 / Light2 / Light1 / Accent / Dark1 / Dark2 / Dark3 / 补色。
    /// <c>DWM\AccentColor</c> 是 0xAABBGGRR 的 DWORD，作为 Accent 的兜底
    /// （极少数机器上 AccentPalette 为空）。
    ///
    /// <c>[SupportedOSPlatform]</c> 不是装饰：本项目是多目标跨平台的（net10.0），
    /// 而 <c>Microsoft.Win32.Registry</c> 在分析器眼里只有 Windows 才可用。
    /// 标注在这里、并在调用点用 <c>OperatingSystem.IsWindows()</c> 分支，
    /// CA1416 才会认这条路径是安全的（否则它是 error 级别，因为本仓库
    /// Directory.Build.props 开了 TreatWarningsAsErrors）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static AccentRamp? ReadWindowsAccentPalette()
    {
        try
        {
            byte[]? palette = null;
            using (var key = Registry.CurrentUser.OpenSubKey(AccentKeyPath))
            {
                palette = key?.GetValue("AccentPalette") as byte[];
            }

            if (palette is { Length: >= 28 })
            {
                var light3 = Rgb(palette, 0);
                var light2 = Rgb(palette, 4);
                var light1 = Rgb(palette, 8);
                var accent = Rgb(palette, 12);
                var dark1 = Rgb(palette, 16);
                var dark2 = Rgb(palette, 20);
                var dark3 = Rgb(palette, 24);

                // 全黑说明这份调色板没被填过（某些精简版系统），交给兜底。
                if ((accent.R | accent.G | accent.B) != 0)
                {
                    return new AccentRamp(accent, light1, light2, light3, dark1, dark2, dark3);
                }
            }

            using var dwm = Registry.CurrentUser.OpenSubKey(DwmKeyPath);
            if (dwm?.GetValue("AccentColor") is int raw)
            {
                // 0xAABBGGRR：低位是 R。
                var value = unchecked((uint)raw);
                var accent = Color.FromArgb(
                    0xFF,
                    (byte)(value & 0xFF),
                    (byte)((value >> 8) & 0xFF),
                    (byte)((value >> 16) & 0xFF));
                if ((accent.R | accent.G | accent.B) != 0)
                {
                    return DeriveRamp(accent);
                }
            }
        }
        catch (Exception ex)
        {
            TryLog($"WindowsColorScheme.ReadWindowsAccentPalette failed: {ex}");
        }

        return null;
    }

    private static Color Rgb(byte[] buffer, int offset) =>
        Color.FromArgb(0xFF, buffer[offset], buffer[offset + 1], buffer[offset + 2]);

    /// <summary>
    /// 从单色派生整条色阶：向白/向黑线性混合。这是 Windows 那套色阶的近似 ——
    /// 只在读不到原生 AccentPalette 时使用（非 Windows 平台，或系统未填调色板）。
    /// </summary>
    private static AccentRamp DeriveRamp(Color accent) => new(
        accent,
        Blend(accent, Colors.White, 0.20),
        Blend(accent, Colors.White, 0.40),
        Blend(accent, Colors.White, 0.60),
        Blend(accent, Colors.Black, 0.20),
        Blend(accent, Colors.Black, 0.40),
        Blend(accent, Colors.Black, 0.60));

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        0xFF,
        (byte)Math.Round((from.R * (1 - amount)) + (to.R * amount)),
        (byte)Math.Round((from.G * (1 - amount)) + (to.G * amount)),
        (byte)Math.Round((from.B * (1 - amount)) + (to.B * amount)));

    /// <summary>
    /// 把一套强调色写进某个主题字典里的全部 AppAccent* 笔刷。
    ///
    /// 为什么是「就地改笔刷的 Color」而不是「把字典里的键换成新笔刷」：
    /// 前者改的是同一个对象，所有引用它的地方（含已经解析过的绑定）立刻跟着变；
    /// 后者只换掉了「键指向谁」，那些在替换前就拿到旧实例的地方会停在旧颜色上 ——
    /// 症状是强调色只更新了一半，而且两半看起来都很正常。
    /// </summary>
    private static void PatchTheme(
        ThemeVariant themeKey,
        Color accent,
        Color accentStrong,
        Color lineColor,
        Color softColor,
        double softOpacity,
        double lineOpacity)
    {
        if (FindTokensDictionary() is not { } tokens ||
            !tokens.ThemeDictionaries.TryGetValue(themeKey, out var provider) ||
            provider is not ResourceDictionary theme)
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
    /// 找到 Tokens.axaml 那一本字典。按 avares 路径精确匹配而不是「取第 0 本」——
    /// 合并顺序一旦调整，按下标取就会静默改错字典（改完什么都没有变化，很难查）。
    /// </summary>
    private static ResourceDictionary? FindTokensDictionary()
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null)
        {
            return null;
        }

        foreach (var dictionary in merged)
        {
            if (dictionary is ResourceDictionary { } candidate && LooksLikeTokens(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>凭「是否同时含主题字典与 AppAccentBrush」认领，比按下标稳。</summary>
    private static bool LooksLikeTokens(ResourceDictionary dictionary) =>
        dictionary.ThemeDictionaries.Count > 0 &&
        dictionary.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var dark) &&
        dark is ResourceDictionary entries &&
        entries.ContainsKey("AppAccentBrush");

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
        if (theme.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
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
