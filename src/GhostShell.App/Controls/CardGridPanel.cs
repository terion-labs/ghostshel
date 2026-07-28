using Avalonia;
using Avalonia.Controls;

namespace GhostShell.App.Controls;

/// <summary>
/// Lays launcher cards out on a gap-consistent grid that stretches to the
/// available width. Cards keep a uniform width per row and flush outer edges, so
/// a row never leaves the ragged trailing gap a fixed-width wrap panel produces.
/// </summary>
public sealed class CardGridPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGridPanel, double>(nameof(MinItemWidth), 240);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<CardGridPanel, double>(nameof(Spacing), 14);

    static CardGridPanel()
    {
        AffectsMeasure<CardGridPanel>(MinItemWidthProperty, SpacingProperty);
    }

    /// <summary>The narrowest a card may become before a column is dropped.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Gap between columns and between rows.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        var metrics = Resolve(availableSize.Width);
        var height = 0d;
        var rowHeight = 0d;

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(metrics.ItemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            var isRowEnd = (index + 1) % metrics.Columns == 0
                || index == Children.Count - 1;
            if (isRowEnd)
            {
                height += height > 0 ? Spacing + rowHeight : rowHeight;
                rowHeight = 0;
            }
        }

        return new Size(metrics.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var metrics = Resolve(finalSize.Width);
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            child.Arrange(new Rect(x, y, metrics.ItemWidth, child.DesiredSize.Height));

            if ((index + 1) % metrics.Columns == 0)
            {
                x = 0;
                y += rowHeight + Spacing;
                rowHeight = 0;
            }
            else
            {
                x += metrics.ItemWidth + Spacing;
            }
        }

        return finalSize;
    }

    private Metrics Resolve(double availableWidth)
    {
        var width = double.IsFinite(availableWidth) && availableWidth > 0
            ? availableWidth
            : MinItemWidth;
        var spacing = Math.Max(0, Spacing);
        var minimum = Math.Max(1, MinItemWidth);
        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (minimum + spacing)));

        // Column count follows the available width, never the number of cards. It
        // used to be clamped to the child count, which made a single card stretch
        // across the entire row — a lone saved screen became a metre-wide bar
        // instead of a card the size of its neighbours.
        var itemWidth = (width - ((columns - 1) * spacing)) / columns;
        return new Metrics(columns, itemWidth, width);
    }

    private readonly record struct Metrics(int Columns, double ItemWidth, double Width);
}
