using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BMSMonitor.Controls;

/// <summary>
/// Grid layout for ItemsRepeater whose column count depends only on the
/// available width (up to <see cref="MaxColumns"/>, never narrower than
/// <see cref="MinItemWidth"/>). Unlike UniformGridLayout it never sizes
/// columns from a measured item, so two repeaters of equal width always get
/// identical tiles (Module 1 / Module 2).
/// </summary>
public sealed class FixedColumnLayout : NonVirtualizingLayout
{
    public int    MaxColumns     { get; set; } = 5;
    public double MinItemWidth   { get; set; } = 100;
    public double MinItemHeight  { get; set; } = 0;
    public double ColumnSpacing  { get; set; } = 10;
    public double RowSpacing     { get; set; } = 10;

    private int    _cols = 1;
    private double _colWidth;
    private readonly List<double> _rowHeights = new();

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        double width = double.IsInfinity(availableSize.Width)
            ? MaxColumns * (MinItemWidth + ColumnSpacing) - ColumnSpacing
            : availableSize.Width;

        _cols = (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing));
        _cols = Math.Clamp(_cols, 1, Math.Max(1, MaxColumns));
        _colWidth = Math.Max(0, (width - ColumnSpacing * (_cols - 1)) / _cols);

        _rowHeights.Clear();
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(_colWidth, double.PositiveInfinity));
            int row = i / _cols;
            if (row >= _rowHeights.Count) _rowHeights.Add(MinItemHeight);
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
        }

        double height = _rowHeights.Sum() + RowSpacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        double y = 0;
        for (int row = 0; row < _rowHeights.Count; row++)
        {
            double h = _rowHeights[row];
            for (int c = 0; c < _cols; c++)
            {
                int i = row * _cols + c;
                if (i >= children.Count) break;
                double x = c * (_colWidth + ColumnSpacing);
                children[i].Arrange(new Rect(x, y, _colWidth, h));
            }
            y += h + RowSpacing;
        }
        return finalSize;
    }
}
