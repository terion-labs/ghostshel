using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GhostShell.Core;

namespace GhostShell.App.Controls;

public sealed class LayoutDesignerGridVisual : Control
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, int>(
            nameof(Columns),
            defaultValue: 1);

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, int>(
            nameof(Rows),
            defaultValue: 1);

    public static readonly StyledProperty<LayoutGridBounds?> PreviewBoundsProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, LayoutGridBounds?>(
            nameof(PreviewBounds));

    public static readonly StyledProperty<IBrush?> GridBackgroundBrushProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, IBrush?>(
            nameof(GridBackgroundBrush));

    public static readonly StyledProperty<IBrush?> GridLineBrushProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, IBrush?>(
            nameof(GridLineBrush));

    public static readonly StyledProperty<IBrush?> PreviewBrushProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, IBrush?>(
            nameof(PreviewBrush));

    public static readonly StyledProperty<IBrush?> PreviewBorderBrushProperty =
        AvaloniaProperty.Register<LayoutDesignerGridVisual, IBrush?>(
            nameof(PreviewBorderBrush));

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public LayoutGridBounds? PreviewBounds
    {
        get => GetValue(PreviewBoundsProperty);
        set => SetValue(PreviewBoundsProperty, value);
    }

    public IBrush? GridBackgroundBrush
    {
        get => GetValue(GridBackgroundBrushProperty);
        set => SetValue(GridBackgroundBrushProperty, value);
    }

    public IBrush? GridLineBrush
    {
        get => GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public IBrush? PreviewBrush
    {
        get => GetValue(PreviewBrushProperty);
        set => SetValue(PreviewBrushProperty, value);
    }

    public IBrush? PreviewBorderBrush
    {
        get => GetValue(PreviewBorderBrushProperty);
        set => SetValue(PreviewBorderBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);
        context.DrawRectangle(
            GridBackgroundBrush ?? Brushes.Transparent,
            null,
            new Rect(Bounds.Size));
        if (GridLineBrush is { } gridLineBrush)
        {
            var pen = new Pen(gridLineBrush, 1);
            for (var column = 1; column < columns; column++)
            {
                var x = Bounds.Width * column / columns;
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
            }

            for (var row = 1; row < rows; row++)
            {
                var y = Bounds.Height * row / rows;
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }

        if (PreviewBounds is not { } previewBounds)
        {
            return;
        }

        var preview = Inset(PixelBounds(previewBounds, columns, rows), 3);
        context.DrawRectangle(
            PreviewBrush,
            PreviewBorderBrush is { } borderBrush
                ? new Pen(borderBrush, 2)
                : null,
            preview);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty
            || change.Property == RowsProperty
            || change.Property == PreviewBoundsProperty
            || change.Property == GridBackgroundBrushProperty
            || change.Property == GridLineBrushProperty
            || change.Property == PreviewBrushProperty
            || change.Property == PreviewBorderBrushProperty)
        {
            InvalidateVisual();
        }
    }

    private Rect PixelBounds(
        LayoutGridBounds bounds,
        int columns,
        int rows)
    {
        var cellWidth = Bounds.Width / columns;
        var cellHeight = Bounds.Height / rows;
        return new Rect(
            bounds.Column * cellWidth,
            bounds.Row * cellHeight,
            bounds.ColumnSpan * cellWidth,
            bounds.RowSpan * cellHeight);
    }

    private static Rect Inset(Rect rect, double amount) =>
        new(
            rect.X + amount,
            rect.Y + amount,
            Math.Max(0, rect.Width - amount * 2),
            Math.Max(0, rect.Height - amount * 2));
}
