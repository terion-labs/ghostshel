using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Controls;

/// <summary>
/// Arranges runtime panel presenters from the immutable durable layout copied
/// into each panel instance. Spans are preserved; no vendor-native type or
/// durable definition object crosses into the visual tree.
///
/// It also owns resizing. The gap between two panels is the handle: the canvas
/// divides itself by the tab's track weights, and dragging a gap moves the
/// boundary between the two tracks it separates. Doing it here rather than with
/// splitter controls keeps the visual tree exactly as it was — a splitter between
/// every pair of a spanning grid's tracks is a lot of controls to place correctly,
/// and none of them would know about spans.
/// </summary>
public sealed class RuntimePanelLayoutPanel : Panel
{
    /// <summary>
    /// The tab whose track weights this canvas divides by. Bound from the view,
    /// because the items panel inherits the window's data context rather than the
    /// tab's.
    /// </summary>
    public static readonly StyledProperty<RuntimeTabViewModel?> TabProperty =
        AvaloniaProperty.Register<RuntimePanelLayoutPanel, RuntimeTabViewModel?>(nameof(Tab));

    /// <summary>How close the pointer must be to a boundary to grab it.</summary>
    private const double GrabTolerance = 5;

    /// <summary>
    /// The floor a track may be dragged to when no panel asks for more. Panels
    /// state their own minimum, and the one that needs the most wins — a constant
    /// let a track shrink under the content it holds, and the panel then drew
    /// outside the space it had been given.
    /// </summary>
    private const double MinimumTrack = 120;

    private Boundary? _hover;
    private Boundary? _drag;
    private Point _dragOrigin;

    public RuntimePanelLayoutPanel()
    {
        // The gaps are not children, so hit testing has to reach the panel itself
        // even where it is showing only background.
        Background = Brushes.Transparent;
    }

    public RuntimeTabViewModel? Tab
    {
        get => GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var required = RequiredSize();
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(availableSize.Width, required.Width)
            : required.Width;
        var height = double.IsFinite(availableSize.Height)
            ? Math.Max(availableSize.Height, required.Height)
            : required.Height;
        var layoutSize = new Size(width, height);
        foreach (var child in Children)
        {
            child.Measure(LayoutBounds(child, layoutSize).Size);
        }

        return layoutSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var required = RequiredSize();
        var layoutSize = new Size(
            Math.Max(finalSize.Width, required.Width),
            Math.Max(finalSize.Height, required.Height));
        foreach (var child in Children)
        {
            child.Arrange(LayoutBounds(child, layoutSize));
        }

        return layoutSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is { } drag)
        {
            var point = e.GetPosition(this);
            var total = drag.IsColumn ? Bounds.Width : Bounds.Height;
            if (total <= 0)
            {
                return;
            }

            var moved = (drag.IsColumn ? point.X - _dragOrigin.X : point.Y - _dragOrigin.Y) / total;
            var minimum = Math.Min(0.45, MinimumPixels(drag) / total);
            var applied = drag.IsColumn
                ? Tab?.MoveColumnSplit(drag.Index, moved, minimum)
                : Tab?.MoveRowSplit(drag.Index, moved, minimum);
            if (applied == true)
            {
                _dragOrigin = point;
                InvalidateArrange();
            }

            e.Handled = true;
            return;
        }

        _hover = FindBoundary(e.GetPosition(this));
        Cursor = _hover is { } boundary
            ? new Cursor(boundary.IsColumn ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth)
            : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FindBoundary(e.GetPosition(this)) is not { } boundary)
        {
            return;
        }

        _drag = boundary;
        _dragOrigin = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag is null)
        {
            Cursor = Cursor.Default;
            _hover = null;
        }
    }

    /// <summary>
    /// The boundary under a point, if the pointer is in a gap between two tracks
    /// rather than over a panel.
    /// </summary>
    private Boundary? FindBoundary(Point point)
    {
        if (Tab is not { } tab || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return null;
        }

        for (var index = 0; index + 1 < tab.ColumnWeights.Count; index++)
        {
            var edge = Offset(tab.ColumnWeights, index + 1) * Bounds.Width;
            if (Math.Abs(point.X - edge) <= GrabTolerance)
            {
                return new Boundary(true, index);
            }
        }

        for (var index = 0; index + 1 < tab.RowWeights.Count; index++)
        {
            var edge = Offset(tab.RowWeights, index + 1) * Bounds.Height;
            if (Math.Abs(point.Y - edge) <= GrabTolerance)
            {
                return new Boundary(false, index);
            }
        }

        return null;
    }

    /// <summary>
    /// How few pixels either track beside this boundary may be reduced to: the
    /// largest minimum among the panels that occupy them, spread over the tracks
    /// each one spans.
    /// </summary>
    private double MinimumPixels(Boundary boundary)
    {
        var needed = MinimumTrack;
        foreach (var child in Children)
        {
            if (child.DataContext is not RuntimePanelViewModel panel)
            {
                continue;
            }

            var start = boundary.IsColumn ? panel.LayoutColumn : panel.LayoutRow;
            var span = Math.Max(1, boundary.IsColumn ? panel.LayoutColumnSpan : panel.LayoutRowSpan);
            if (boundary.Index + 1 < start || boundary.Index >= start + span)
            {
                continue;
            }

            var minimum = boundary.IsColumn ? panel.LayoutMinimumWidth : panel.LayoutMinimumHeight;
            needed = Math.Max(needed, minimum / span);
        }

        return needed;
    }

    private static double Offset(IReadOnlyList<double> weights, int track)
    {
        var total = 0d;
        for (var index = 0; index < track && index < weights.Count; index++)
        {
            total += weights[index];
        }

        return total;
    }

    private Size RequiredSize()
    {
        var panels = Children
            .Select(child => child.DataContext as RuntimePanelViewModel)
            .Where(panel => panel is not null)
            .Cast<RuntimePanelViewModel>()
            .ToArray();
        if (panels.Length == 0)
        {
            return default;
        }

        var columns = Math.Max(1, panels.Max(panel => panel.LayoutColumns));
        var rows = Math.Max(1, panels.Max(panel => panel.LayoutRows));
        var cellWidth = panels.Max(panel =>
            panel.LayoutMinimumWidth / Math.Max(1, panel.LayoutColumnSpan));
        var cellHeight = panels.Max(panel =>
            panel.LayoutMinimumHeight / Math.Max(1, panel.LayoutRowSpan));
        return new Size(columns * cellWidth, rows * cellHeight);
    }

    private Rect LayoutBounds(Control child, Size size)
    {
        if (child.DataContext is not RuntimePanelViewModel panel)
        {
            return new Rect(size);
        }

        if (panel.IsZoomed)
        {
            return new Rect(size);
        }

        var columns = Math.Max(1, panel.LayoutColumns);
        var rows = Math.Max(1, panel.LayoutRows);
        var columnWeights = TrackWeights(Tab?.ColumnWeights, columns);
        var rowWeights = TrackWeights(Tab?.RowWeights, rows);

        var left = Offset(columnWeights, panel.LayoutColumn) * size.Width;
        var top = Offset(rowWeights, panel.LayoutRow) * size.Height;
        var right = Offset(columnWeights, panel.LayoutColumn + panel.LayoutColumnSpan) * size.Width;
        var bottom = Offset(rowWeights, panel.LayoutRow + panel.LayoutRowSpan) * size.Height;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The tab's weights when they describe this many tracks, and an even division
    /// otherwise — a panel whose layout disagrees with the tab's is still drawn.
    /// </summary>
    private static IReadOnlyList<double> TrackWeights(IReadOnlyList<double>? weights, int count)
    {
        if (weights is { Count: > 0 } && weights.Count == count)
        {
            return weights;
        }

        var even = new double[Math.Max(1, count)];
        Array.Fill(even, 1d / even.Length);
        return even;
    }

    private readonly record struct Boundary(bool IsColumn, int Index);
}
