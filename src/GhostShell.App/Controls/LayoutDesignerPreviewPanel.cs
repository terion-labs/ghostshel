using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Controls;

/// <summary>
/// Projects immutable layout slots onto their logical grid and owns the pointer gesture draft.
/// Durable geometry still changes only through <see cref="LayoutDesignerViewModel"/>.
/// </summary>
public sealed class LayoutDesignerPreviewPanel : Panel
{
    private const double EdgeHitZone = 9;

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor HorizontalResizeCursor =
        new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor =
        new(StandardCursorType.SizeNorthSouth);

    private PointerGesture? _gesture;

    public LayoutDesignerPreviewPanel()
    {
        ClipToBounds = true;
        AddHandler(
            PointerPressedEvent,
            OnPreviewPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerMovedEvent,
            OnPreviewPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            OnPreviewPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 520,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 420);
        foreach (var child in Children)
        {
            child.Measure(LayoutBounds(child, size).Size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(LayoutBounds(child, finalSize));
        }

        return finalSize;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture is { } gesture
            && OwnsPointer(gesture.Pointer, e.Pointer))
        {
            ClearGesture();
        }
    }

    internal bool CancelPointerGesture()
    {
        var pointer = _gesture?.Pointer;
        if (pointer is null)
        {
            return false;
        }

        ClearGesture();
        pointer.Capture(null);
        return true;
    }

    internal static bool OwnsPointer(object owner, object candidate) =>
        ReferenceEquals(owner, candidate);

    internal static LayoutGridBounds NormalizePaintBounds(
        int anchorColumn,
        int anchorRow,
        int currentColumn,
        int currentRow)
    {
        var column = Math.Min(anchorColumn, currentColumn);
        var row = Math.Min(anchorRow, currentRow);
        return new LayoutGridBounds(
            column,
            row,
            Math.Abs(currentColumn - anchorColumn) + 1,
            Math.Abs(currentRow - anchorRow) + 1);
    }

    internal static LayoutGridBounds SnapResizeBounds(
        LayoutGridBounds original,
        LayoutDesignerEdge edge,
        Point position,
        Size canvas,
        int columns,
        int rows)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (columns < 1 || rows < 1 || canvas.Width <= 0 || canvas.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canvas),
                "A pointer grid requires positive dimensions.");
        }

        var right = original.Column + original.ColumnSpan;
        var bottom = original.Row + original.RowSpan;
        var horizontalBoundary = SnapBoundary(position.X, canvas.Width / columns, columns);
        var verticalBoundary = SnapBoundary(position.Y, canvas.Height / rows, rows);
        return edge switch
        {
            LayoutDesignerEdge.Left => new(
                Math.Clamp(horizontalBoundary, 0, right - 1),
                original.Row,
                right - Math.Clamp(horizontalBoundary, 0, right - 1),
                original.RowSpan),
            LayoutDesignerEdge.Right => new(
                original.Column,
                original.Row,
                Math.Clamp(horizontalBoundary, original.Column + 1, columns)
                    - original.Column,
                original.RowSpan),
            LayoutDesignerEdge.Top => new(
                original.Column,
                Math.Clamp(verticalBoundary, 0, bottom - 1),
                original.ColumnSpan,
                bottom - Math.Clamp(verticalBoundary, 0, bottom - 1)),
            LayoutDesignerEdge.Bottom => new(
                original.Column,
                original.Row,
                original.ColumnSpan,
                Math.Clamp(verticalBoundary, original.Row + 1, rows)
                    - original.Row),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    internal static LayoutDesignerEdge? HitTestEdge(
        Rect bounds,
        Point position,
        double hitZone = EdgeHitZone)
    {
        if (!bounds.Contains(position) || hitZone <= 0)
        {
            return null;
        }

        var threshold = Math.Min(
            hitZone,
            Math.Min(bounds.Width, bounds.Height) / 3);
        var candidates = new[]
        {
            (Edge: LayoutDesignerEdge.Left, Distance: Math.Abs(position.X - bounds.Left)),
            (Edge: LayoutDesignerEdge.Right, Distance: Math.Abs(position.X - bounds.Right)),
            (Edge: LayoutDesignerEdge.Top, Distance: Math.Abs(position.Y - bounds.Top)),
            (Edge: LayoutDesignerEdge.Bottom, Distance: Math.Abs(position.Y - bounds.Bottom)),
        };
        var closest = candidates.MinBy(candidate => candidate.Distance);
        return closest.Distance <= threshold
            ? closest.Edge
            : null;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        var point = e.GetCurrentPoint(this);
        var editor = DataContext as LayoutDesignerViewModel;
        if (_gesture is not null
            || editor is null
            || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        var slot = SlotAt(editor, position);
        if (slot is not null)
        {
            _ = editor.SelectSlot(slot.Id);
            var edge = HitTestEdge(
                PixelBounds(slot.Bounds, Bounds.Size, editor.Columns, editor.Rows),
                position);
            if (edge is null)
            {
                return;
            }

            BeginGesture(
                e,
                new PointerGesture(
                    e.Pointer,
                    PointerGestureKind.Resize,
                    slot.Id,
                    slot.Bounds,
                    edge.Value,
                    AnchorColumn: 0,
                    AnchorRow: 0,
                    slot.Bounds,
                    editor.Columns,
                    editor.Rows));
            return;
        }

        if (!editor.IsPaintMode)
        {
            return;
        }

        var cell = CellAt(position, Bounds.Size, editor.Columns, editor.Rows);
        var bounds = new LayoutGridBounds(cell.Column, cell.Row, 1, 1);
        BeginGesture(
            e,
            new PointerGesture(
                e.Pointer,
                PointerGestureKind.Paint,
                SlotId: null,
                OriginalBounds: bounds,
                Edge: null,
                cell.Column,
                cell.Row,
                bounds,
                editor.Columns,
                editor.Rows));
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        var editor = DataContext as LayoutDesignerViewModel;
        if (editor is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_gesture is { } gesture)
        {
            if (!OwnsPointer(gesture.Pointer, e.Pointer))
            {
                return;
            }

            if (gesture.Kind == PointerGestureKind.Paint && !editor.IsPaintMode)
            {
                ClearGesture();
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }

            var preview = gesture.Kind == PointerGestureKind.Paint
                ? PaintBounds(gesture, position)
                : SnapResizeBounds(
                    gesture.OriginalBounds,
                    gesture.Edge!.Value,
                    position,
                    Bounds.Size,
                    gesture.Columns,
                    gesture.Rows);
            if (preview != gesture.PreviewBounds)
            {
                _gesture = gesture with { PreviewBounds = preview };
                if (gesture.Kind == PointerGestureKind.Paint)
                {
                    editor.SetPaintPreviewBounds(preview);
                }

                InvalidateArrange();
            }

            e.Handled = true;
            return;
        }

        UpdateCursor(editor, position);
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (_gesture is not { } gesture
            || !OwnsPointer(gesture.Pointer, e.Pointer))
        {
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            e.Handled = true;
            return;
        }

        var editor = DataContext as LayoutDesignerViewModel;
        var finalBounds = gesture.Kind == PointerGestureKind.Paint
            ? PaintBounds(gesture, e.GetPosition(this))
            : SnapResizeBounds(
                gesture.OriginalBounds,
                gesture.Edge!.Value,
                e.GetPosition(this),
                Bounds.Size,
                gesture.Columns,
                gesture.Rows);
        _gesture = null;
        editor?.SetPaintPreviewBounds(null);
        InvalidateArrange();
        e.Pointer.Capture(null);
        e.Handled = true;

        if (editor is null)
        {
            return;
        }

        if (gesture.Kind == PointerGestureKind.Paint)
        {
            if (editor.IsPaintMode)
            {
                _ = editor.AddSlot(finalBounds);
            }

            return;
        }

        if (gesture.SlotId is not { } slotId
            || gesture.Edge is null)
        {
            return;
        }

        if (finalBounds != gesture.OriginalBounds)
        {
            _ = editor.ReplaceSlotBounds(
                slotId,
                gesture.OriginalBounds,
                finalBounds);
        }
    }

    private void BeginGesture(PointerPressedEventArgs e, PointerGesture gesture)
    {
        _gesture = gesture;
        if (gesture.Kind == PointerGestureKind.Paint)
        {
            (DataContext as LayoutDesignerViewModel)?.SetPaintPreviewBounds(
                gesture.PreviewBounds);
        }

        _ = this.GetVisualAncestors()
            .OfType<ItemsControl>()
            .FirstOrDefault()
            ?.Focus();
        e.PreventGestureRecognition();
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateArrange();
    }

    private void ClearGesture()
    {
        if (_gesture is null)
        {
            return;
        }

        _gesture = null;
        (DataContext as LayoutDesignerViewModel)?.SetPaintPreviewBounds(null);
        InvalidateArrange();
    }

    private Rect LayoutBounds(Control child, Size size)
    {
        if (child.DataContext is not LayoutDesignerSlotViewModel slot)
        {
            return new Rect(size);
        }

        var editor = DataContext as LayoutDesignerViewModel;
        var columns = Math.Max(1, editor?.Columns ?? 1);
        var rows = Math.Max(1, editor?.Rows ?? 1);
        var bounds = _gesture is
        {
            Kind: PointerGestureKind.Resize,
            SlotId: { } slotId,
        } gesture
            && slotId == slot.Id
                ? gesture.PreviewBounds
                : slot.Bounds;
        return PixelBounds(bounds, size, columns, rows);
    }

    private LayoutDesignerSlotViewModel? SlotAt(
        LayoutDesignerViewModel editor,
        Point position) =>
        editor.Slots.FirstOrDefault(slot =>
            PixelBounds(
                slot.Bounds,
                Bounds.Size,
                editor.Columns,
                editor.Rows).Contains(position));

    private void UpdateCursor(LayoutDesignerViewModel editor, Point position)
    {
        var slot = SlotAt(editor, position);
        var edge = slot is null
            ? null
            : HitTestEdge(
                PixelBounds(slot.Bounds, Bounds.Size, editor.Columns, editor.Rows),
                position);
        Cursor = edge switch
        {
            LayoutDesignerEdge.Left or LayoutDesignerEdge.Right =>
                HorizontalResizeCursor,
            LayoutDesignerEdge.Top or LayoutDesignerEdge.Bottom =>
                VerticalResizeCursor,
            null when slot is null && editor.IsPaintMode => CrossCursor,
            _ => ArrowCursor,
        };
    }

    private LayoutGridBounds PaintBounds(
        PointerGesture gesture,
        Point position)
    {
        var cell = CellAt(
            position,
            Bounds.Size,
            gesture.Columns,
            gesture.Rows);
        return NormalizePaintBounds(
            gesture.AnchorColumn,
            gesture.AnchorRow,
            cell.Column,
            cell.Row);
    }

    private static (int Column, int Row) CellAt(
        Point position,
        Size canvas,
        int columns,
        int rows)
    {
        var column = CellIndex(position.X, canvas.Width, columns);
        var row = CellIndex(position.Y, canvas.Height, rows);
        return (column, row);
    }

    private static int CellIndex(double coordinate, double extent, int count)
    {
        if (count < 1 || extent <= 0 || !double.IsFinite(coordinate))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(coordinate / extent * count), 0, count - 1);
    }

    private static int SnapBoundary(double coordinate, double cellExtent, int limit)
    {
        if (cellExtent <= 0 || !double.IsFinite(coordinate))
        {
            return 0;
        }

        return Math.Clamp(
            (int)Math.Round(
                coordinate / cellExtent,
                MidpointRounding.AwayFromZero),
            0,
            limit);
    }

    private static Rect PixelBounds(
        LayoutGridBounds bounds,
        Size size,
        int columns,
        int rows)
    {
        var cellWidth = size.Width / Math.Max(1, columns);
        var cellHeight = size.Height / Math.Max(1, rows);
        return new Rect(
            bounds.Column * cellWidth,
            bounds.Row * cellHeight,
            bounds.ColumnSpan * cellWidth,
            bounds.RowSpan * cellHeight);
    }

    private enum PointerGestureKind
    {
        Paint,
        Resize,
    }

    private sealed record PointerGesture(
        IPointer Pointer,
        PointerGestureKind Kind,
        LayoutSlotId? SlotId,
        LayoutGridBounds OriginalBounds,
        LayoutDesignerEdge? Edge,
        int AnchorColumn,
        int AnchorRow,
        LayoutGridBounds PreviewBounds,
        int Columns,
        int Rows);
}
