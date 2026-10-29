using System;
using Avalonia;
using Avalonia.Controls;

namespace IotExpertApp.Views;

// ширина приходит от области внутри ScrollViewer; меряем плитки одинаково, горизонтальную прокрутку не создаем
public sealed class ModuleTilePanel : Panel {
    private const double Gap = 12;
    public static int Columns(double width) => width >= 840 ? 3 : width >= 680 ? 2 : 1;
    protected override Size MeasureOverride(Size availableSize) {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1000;
        int columns = Columns(width);
        double cell = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        double height = 0;
        for (int i = 0; i < Children.Count; i += columns) {
            double row = 0;
            for (int j = i; j < Math.Min(i + columns, Children.Count); j++) {
                Children[j].Measure(new Size(cell, double.PositiveInfinity)); row = Math.Max(row, Children[j].DesiredSize.Height);
            }
            height += row + (i == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize) {
        int columns = Columns(finalSize.Width);
        double cell = Math.Max(0, (finalSize.Width - (columns - 1) * Gap) / columns), y = 0;
        for (int i = 0; i < Children.Count; i += columns) {
            double height = 0;
            for (int j = i; j < Math.Min(i + columns, Children.Count); j++) height = Math.Max(height, Children[j].DesiredSize.Height);
            for (int j = i; j < Math.Min(i + columns, Children.Count); j++) Children[j].Arrange(new Rect((j - i) * (cell + Gap), y, cell, height));
            y += height + Gap;
        }
        return finalSize;
    }
}

public sealed class ModuleCheckLayout : Panel {
    protected override Size MeasureOverride(Size availableSize) {
        if (Children.Count != 2) return default;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1000;
        bool side = width >= 1150;
        Children[0].Measure(new Size(side ? width - 332 : width, double.PositiveInfinity));
        Children[1].Measure(new Size(side ? 320 : width, double.PositiveInfinity));
        return new Size(width, side ? Math.Max(Children[0].DesiredSize.Height, Children[1].DesiredSize.Height)
            : Children[0].DesiredSize.Height + Children[1].DesiredSize.Height + 12);
    }
    protected override Size ArrangeOverride(Size size) {
        if (Children.Count != 2) return size;
        if (size.Width >= 1150) {
            Children[0].Arrange(new Rect(0, 0, size.Width - 332, Children[0].DesiredSize.Height));
            Children[1].Arrange(new Rect(size.Width - 320, 0, 320, Children[1].DesiredSize.Height));
        }
        else {
            Children[0].Arrange(new Rect(0, 0, size.Width, Children[0].DesiredSize.Height));
            Children[1].Arrange(new Rect(0, Children[0].DesiredSize.Height + 12, size.Width, Children[1].DesiredSize.Height));
        }
        return size;
    }
}
