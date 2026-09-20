using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 字体图标。WinUI 的 <c>FontIcon</c> 在 Avalonia 里没有对应控件，而本项目有二十余处
/// 图标依赖 <c>Segoe Fluent Icons</c> 的字形（E91B 画廊 / E740 灯箱 / E713 设置 …）。
///
/// 为什么不改成矢量 <c>Path</c>：那会把「图标长什么样」从系统字形变成手抄的轮廓，
/// 二十多个图标里错一个就整屏串味，而且字重随字号缩放的行为也变了。
/// 保留同一套字形 = 图标与 WinUI 版逐像素一致（本机实测该字族可被 Avalonia 解析）。
///
/// 之所以包一层而不是直接用 <c>TextBlock</c>：XAML 里 <c>&lt;FontIcon Glyph="…"/&gt;</c>
/// 与 WinUI 版逐字对应，迁移 diff 才可读；字形字族的绑定也集中在一处。
/// </summary>
public sealed class FontIcon : TextBlock
{
    /// <summary>图标字形（Segoe Fluent Icons 码位，如 <c>&amp;#xE91B;</c>）。</summary>
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<FontIcon, string?>(nameof(Glyph));

    public FontIcon()
    {
        TextAlignment = TextAlignment.Center;
        TextWrapping = TextWrapping.NoWrap;
        // 图标默认取中心对齐：放在按钮 / 胶囊里时与文字基线不打架。
        // 必须用全限定名：HorizontalAlignment 这个「属性名」与枚举类型同名，
        // 裸写右侧会被解析成实例属性访问（CS0176），而只写 Layout. 也不解析
        // —— using Avalonia; 并不会把嵌套命名空间 Avalonia.Layout 带成简名。
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        // 字族必须在这里落地，不能靠继承。
        // 这些码位（E91B / E740 / E713 …）只存在于 Segoe Fluent Icons / Segoe MDL2 Assets，
        // 而 Avalonia 默认字族是 Segoe UI（不含 PUA 字形）—— 不显式指定的话
        // 二十多个图标会全部渲染成豆腐块，而且因为「有内容、占位正确」，
        // 光看布局很难发现是字体问题。
        // 这里设的是本地值：XAML 里显式写 FontFamily 依然可以覆盖它。
        FontFamily = IconFontFamily;
    }

    /// <summary>图标字族（与设计令牌 AppFontIcon 同一条回退链）。</summary>
    private static readonly FontFamily IconFontFamily =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>图标字形。</summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphProperty)
        {
            Text = Glyph ?? string.Empty;
        }
    }
}
