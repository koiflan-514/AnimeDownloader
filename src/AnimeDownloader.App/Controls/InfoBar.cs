using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace AnimeDownloader.App.Controls;

/// <summary>信息条的语义等级。</summary>
public enum InfoBarSeverity
{
    /// <summary>提示（信息）。</summary>
    Informational,

    /// <summary>成功。</summary>
    Success,

    /// <summary>警告 —— 在本设计里就是「安全灯亮起」：需要你注意。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>
/// 信息条。Avalonia 没有 <c>InfoBar</c>，这里按原版用到的能力重建：
/// 标题 / 正文 / 语义等级 / 可关闭 / 一个动作按钮（就是 <see cref="ContentControl.Content"/>）。
///
/// 语义配色沿用暗房的三档状态语汇，而不是 WinUI 的 SystemFillColor*：
///     信息 → 次要文本灰（安静）；成功 → 状态灰；警告 → **强调色（安全灯）**；错误 → 状态红。
/// 这条是有意为之：设计规范里「需要你注意」的语义永远由强调色承担，
/// 引入第二个暖色（WinUI 的琥珀 caution）会让「全应用只有一个彩色」这条硬规则失效。
/// </summary>
public sealed class InfoBar : ContentControl
{
    /// <summary>是否显示。</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<InfoBar, bool>(nameof(IsOpen));

    /// <summary>标题。</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InfoBar, string?>(nameof(Title));

    /// <summary>正文。</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<InfoBar, string?>(nameof(Message));

    /// <summary>语义等级。</summary>
    public static readonly StyledProperty<InfoBarSeverity> SeverityProperty =
        AvaloniaProperty.Register<InfoBar, InfoBarSeverity>(nameof(Severity));

    /// <summary>是否显示关闭按钮。</summary>
    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<InfoBar, bool>(nameof(IsClosable), true);

    /// <summary>
    /// 动作区内容。对应 WinUI 的 <c>InfoBar.ActionButton</c> ——
    /// 那个属性是 <c>ICommandBarElement</c> 且只吃 ButtonBase，这里放宽成任意内容：
    /// 本应用只有一处用到（画廊欢迎条上的「了解设置」），用 <see cref="ContentControl.Content"/>
    /// 会把「正文」占掉，因此单独开一个属性。
    /// </summary>
    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<InfoBar, object?>(nameof(ActionContent));

    /// <summary>由语义等级推导出的字形（供模板绑定）。</summary>
    public static readonly StyledProperty<string?> SeverityGlyphProperty =
        AvaloniaProperty.Register<InfoBar, string?>(nameof(SeverityGlyph), "\uE946");

    /// <summary>由语义等级推导出的颜色（供模板绑定）。</summary>
    public static readonly StyledProperty<IBrush?> SeverityBrushProperty =
        AvaloniaProperty.Register<InfoBar, IBrush?>(nameof(SeverityBrush));

    /// <summary>用户关闭了信息条。</summary>
    public event EventHandler? Closed;

    public InfoBar()
    {
        IsVisible = false;
        // 主题切换后要重新解析语义色（DynamicResource 在这里够不着，只能自己重取）
        ActualThemeVariantChanged += (_, _) => ApplySeverity();
    }

    /// <summary>是否显示。</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>标题。</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>正文。</summary>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>语义等级。</summary>
    public InfoBarSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>是否显示关闭按钮。</summary>
    public bool IsClosable
    {
        get => GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    /// <summary>动作区内容（正文下方的可交互元素）。</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>语义字形。</summary>
    public string? SeverityGlyph
    {
        get => GetValue(SeverityGlyphProperty);
        private set => SetValue(SeverityGlyphProperty, value);
    }

    /// <summary>语义颜色。</summary>
    public IBrush? SeverityBrush
    {
        get => GetValue(SeverityBrushProperty);
        private set => SetValue(SeverityBrushProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_CloseButton") is { } close)
        {
            close.Click += (_, _) => Close();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            IsVisible = IsOpen;
        }
        else if (change.Property == SeverityProperty)
        {
            ApplySeverity();
        }
    }

    /// <summary>关闭信息条并通知宿主（宿主据此把「已看过」写进设置）。</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySeverity()
    {
        var (glyph, brushKey) = Severity switch
        {
            InfoBarSeverity.Success => ("\uE73E", "AppStatusOkBrush"),
            InfoBarSeverity.Warning => ("\uE7BA", "AppAccentBrush"),
            InfoBarSeverity.Error => ("\uE783", "AppStatusErrorBrush"),
            _ => ("\uE946", "AppTextSecondaryBrush"),
        };

        SeverityGlyph = glyph;
        SeverityBrush = ResolveBrush(brushKey);
    }

    private IBrush? ResolveBrush(string key)
    {
        // Avalonia 12 没有 TryFindResource 这个扩展方法，只有 IResourceHost.TryGetResource
        // （它要求显式传入主题变体）。这里先从自身所在的资源链找，再退回应用级。
        if (TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        if (Application.Current is { } app &&
            app.TryGetResource(key, ActualThemeVariant, out var themed) &&
            themed is IBrush themedBrush)
        {
            return themedBrush;
        }

        return null;
    }
}
