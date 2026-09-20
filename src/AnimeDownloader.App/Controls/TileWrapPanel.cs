using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 按固定瓦片尺寸横向换行的面板 —— 对应 WinUI 的 <c>ItemsWrapGrid</c>。
///
/// Avalonia 自带的 <c>WrapPanel</c> 虽然也有 ItemWidth/ItemHeight，但它在
/// Arrange 阶段会把子元素**拉到瓦片满宽**，GalleryPage 那种「卡片 = 瓦片 − 栏距并居中」
/// 的算法就落不了地（栏距会永远不出现，正是 design/DESIGN.md §5.1 里修过的那个老 bug）。
/// 这里改成 ItemsWrapGrid 的语义：**给子元素一个恰好 ItemWidth×ItemHeight 的槽位，
/// 子元素在这个槽位里按自己的 Alignment 摆放**。
///
/// 于是栏距的规则原样成立：
///     瓦片 = 行宽 ÷ 列数（精确铺满，不会多出一格去换行）
///     卡片 = 瓦片 − CardGutter（HorizontalAlignment=Center）
///     相邻两张之间自然是 CardGutter，最外两侧各让出 CardGutter/2 —— 左右对称。
///
/// 列数用 <c>Floor</c> 而不是四舍五入：面板实际拿到的是 ScrollViewer 的视口宽度，
/// 它比控件自身宽度略小一点点。若按除法原值取整，n 个瓦片会刚好超出视口 ——
/// 症状是**最右一列整列消失**（每行只排满 n−1 张），极易误判成「列数算错」。
/// </summary>
public sealed class TileWrapPanel : Panel
{
    /// <summary>单个瓦片的宽度。</summary>
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<TileWrapPanel, double>(nameof(ItemWidth), 248);

    /// <summary>单个瓦片的高度。</summary>
    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<TileWrapPanel, double>(nameof(ItemHeight), 248);

    static TileWrapPanel()
    {
        AffectsMeasure<TileWrapPanel>(ItemWidthProperty, ItemHeightProperty);
        AffectsArrange<TileWrapPanel>(ItemWidthProperty, ItemHeightProperty);
    }

    /// <summary>瓦片宽度（物理像素无关，单位是布局像素）。</summary>
    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>瓦片高度。</summary>
    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemWidth = ItemWidth > 0 ? ItemWidth : 1;
        var itemHeight = ItemHeight > 0 ? ItemHeight : itemWidth;
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var columns = Columns(width, itemWidth);

        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, itemHeight));
        }

        var rows = Children.Count == 0 ? 0 : (int)Math.Ceiling(Children.Count / (double)columns);
        return new Size(width, rows * itemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = ItemWidth > 0 ? ItemWidth : 1;
        var itemHeight = ItemHeight > 0 ? ItemHeight : itemWidth;
        var columns = Columns(finalSize.Width, itemWidth);

        for (var i = 0; i < Children.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            // 槽位给满，子元素用对齐方式在槽位里自处（卡片居中 → 栏距自然出现）
            Children[i].Arrange(new Rect(column * itemWidth, row * itemHeight, itemWidth, itemHeight));
        }

        return finalSize;
    }

    private static int Columns(double width, double itemWidth) =>
        width <= 0 ? 1 : Math.Max(1, (int)Math.Floor(width / itemWidth));
}
