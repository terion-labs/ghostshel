using Avalonia;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class LayoutDesignerPreviewPanelTests
{
    [Fact]
    public void Pointer_surface_is_not_a_duplicate_focus_stop()
    {
        var panel = new LayoutDesignerPreviewPanel();

        Assert.False(panel.Focusable);
        Assert.False(panel.CancelPointerGesture());
    }

    [Fact]
    public void Gesture_ownership_uses_the_initiating_pointer_identity()
    {
        var owner = new object();
        var secondContact = new object();

        Assert.True(LayoutDesignerPreviewPanel.OwnsPointer(owner, owner));
        Assert.False(LayoutDesignerPreviewPanel.OwnsPointer(owner, secondContact));
    }

    [Fact]
    public void Reverse_paint_drag_normalizes_to_one_grid_rectangle()
    {
        var bounds = LayoutDesignerPreviewPanel.NormalizePaintBounds(
            anchorColumn: 4,
            anchorRow: 3,
            currentColumn: 1,
            currentRow: 1);

        Assert.Equal(new LayoutGridBounds(1, 1, 4, 3), bounds);
    }

    [Theory]
    [InlineData((int)LayoutDesignerEdge.Left, 0, 1, 3, 2)]
    [InlineData((int)LayoutDesignerEdge.Right, 1, 1, 4, 2)]
    [InlineData((int)LayoutDesignerEdge.Top, 1, 0, 2, 3)]
    [InlineData((int)LayoutDesignerEdge.Bottom, 1, 1, 2, 4)]
    public void Edge_drag_snaps_to_grid_boundaries(
        int edge,
        int expectedColumn,
        int expectedRow,
        int expectedColumnSpan,
        int expectedRowSpan)
    {
        var original = new LayoutGridBounds(1, 1, 2, 2);
        var pointer = (LayoutDesignerEdge)edge switch
        {
            LayoutDesignerEdge.Left => new Point(0, 200),
            LayoutDesignerEdge.Right => new Point(500, 200),
            LayoutDesignerEdge.Top => new Point(200, 0),
            LayoutDesignerEdge.Bottom => new Point(200, 500),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };

        var resized = LayoutDesignerPreviewPanel.SnapResizeBounds(
            original,
            (LayoutDesignerEdge)edge,
            pointer,
            new Size(500, 500),
            columns: 5,
            rows: 5);

        Assert.Equal(
            new LayoutGridBounds(
                expectedColumn,
                expectedRow,
                expectedColumnSpan,
                expectedRowSpan),
            resized);
    }

    [Fact]
    public void Reverse_edge_drag_clamps_before_a_zero_span()
    {
        var original = new LayoutGridBounds(1, 1, 2, 2);

        var resized = LayoutDesignerPreviewPanel.SnapResizeBounds(
            original,
            LayoutDesignerEdge.Left,
            new Point(500, 200),
            new Size(500, 500),
            columns: 5,
            rows: 5);

        Assert.Equal(new LayoutGridBounds(2, 1, 1, 2), resized);
    }

    [Fact]
    public void Edge_hit_testing_ignores_the_panel_center()
    {
        var bounds = new Rect(100, 100, 200, 200);

        Assert.Equal(
            LayoutDesignerEdge.Left,
            LayoutDesignerPreviewPanel.HitTestEdge(
                bounds,
                new Point(104, 180)));
        Assert.Equal(
            LayoutDesignerEdge.Bottom,
            LayoutDesignerPreviewPanel.HitTestEdge(
                bounds,
                new Point(220, 296)));
        Assert.Null(LayoutDesignerPreviewPanel.HitTestEdge(
            bounds,
            new Point(200, 200)));
    }

}
