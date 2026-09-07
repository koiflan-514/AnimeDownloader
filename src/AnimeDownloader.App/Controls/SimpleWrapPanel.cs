using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 按可用宽度横向换行的简单流式面板（WinUI 3 无内置 WrapPanel），
/// 用于查看器的标签 chip 布局。
/// </summary>
public sealed class SimpleWrapPanel : Panel
{
    /// <summary>水平间距。</summary>
    public double HorizontalSpacing { get; set; } = 6;

    /// <summary>垂直间距。</summary>
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var width = child.DesiredSize.Width;
            var height = child.DesiredSize.Height;
            if (x > 0 && x + width > maxWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            x += width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, height);
        }

        var totalHeight = Children.Count == 0 ? 0 : y + rowHeight;
        return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            var height = child.DesiredSize.Height;
            if (x > 0 && x + width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, width, height));
            x += width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, height);
        }

        return finalSize;
    }
}
