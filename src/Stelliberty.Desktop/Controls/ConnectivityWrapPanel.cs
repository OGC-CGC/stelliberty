using System;
using Avalonia;
using Avalonia.Controls;

namespace Stelliberty.Desktop.Controls;

// 每行最多四项，末行按实际项数均分宽度。
public sealed class ConnectivityWrapPanel : Panel
{
    private const int Columns = 4;
    private const double Spacing = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = 0d;
        for (var start = 0; start < Children.Count; start += Columns)
        {
            var count = Math.Min(Columns, Children.Count - start);
            var itemWidth = Math.Max(0, (width - (count - 1) * Spacing) / count);
            var rowHeight = 0d;
            for (var i = start; i < start + count; i++)
            {
                Children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            height += rowHeight + (start == 0 ? 0 : Spacing);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        for (var start = 0; start < Children.Count; start += Columns)
        {
            var count = Math.Min(Columns, Children.Count - start);
            var itemWidth = Math.Max(0, (finalSize.Width - (count - 1) * Spacing) / count);
            var rowHeight = 0d;
            for (var i = start; i < start + count; i++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            var left = 0d;
            for (var column = 0; column < count; column++)
            {
                var right = (column + 1) * itemWidth + column * Spacing;
                var child = Children[start + column];
                child.Arrange(new Rect(new Point(left, top), new Point(right, top + rowHeight)));
                left = child.Bounds.Right + Spacing;
            }

            top += rowHeight + Spacing;
        }

        return finalSize;
    }
}
