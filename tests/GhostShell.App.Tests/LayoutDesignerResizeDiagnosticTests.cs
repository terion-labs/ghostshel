using System.Reflection;
using Avalonia;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class LayoutDesignerResizeDiagnosticTests
{
    private static readonly Size Canvas = new(560, 324);

    private static LayoutDesignerViewModel SinglePanelFillingTheGrid()
    {
        var definition = new LayoutDefinition(
            new LayoutId("full"),
            LayoutDefinition.CurrentSchemaVersion,
            "Full",
            new LayoutGrid(12, 8),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("only"),
                    new LayoutGridBounds(0, 0, 12, 8),
                    new LayoutMinimumSize(160, 90)),
            ]);
        return new LayoutDesignerViewModel(definition, expectedRevision: 1);
    }

    [Fact]
    public void Right_edge_of_a_full_width_panel_is_grabbable()
    {
        var rect = new Rect(0, 0, Canvas.Width, Canvas.Height);

        Assert.Equal(
            LayoutDesignerEdge.Right,
            LayoutDesignerPreviewPanel.HitTestEdge(rect, new Point(559, 162)));
        Assert.Equal(
            LayoutDesignerEdge.Right,
            LayoutDesignerPreviewPanel.HitTestEdge(rect, new Point(560, 162)));
    }

    [Fact]
    public void Dragging_the_right_edge_inward_shrinks_the_panel()
    {
        var editor = SinglePanelFillingTheGrid();
        var slot = editor.Slots[0];

        var resized = LayoutDesignerPreviewPanel.SnapResizeBounds(
            slot.Bounds,
            LayoutDesignerEdge.Right,
            new Point(280, 162),
            Canvas,
            editor.Columns,
            editor.Rows);

        Assert.Equal(new LayoutGridBounds(0, 0, 6, 8), resized);

        var result = editor.ReplaceSlotBounds(slot.Id, slot.Bounds, resized);

        Assert.True(result.IsSuccess, result.Issue?.Message);
        Assert.Equal(resized, editor.Slots[0].Bounds);
    }

    [Fact]
    public void A_panel_can_be_added_when_one_panel_fills_the_whole_grid()
    {
        var editor = SinglePanelFillingTheGrid();

        var result = editor.AddSlot();

        Assert.True(result.IsSuccess, result.Issue?.Message);
        Assert.Equal(2, editor.Slots.Count);
    }
}

public sealed class LayoutDesignerMoveTests
{
    private static LayoutDesignerViewModel TwoPanelsSideBySide()
    {
        var definition = new LayoutDefinition(
            new LayoutId("pair"),
            LayoutDefinition.CurrentSchemaVersion,
            "Pair",
            new LayoutGrid(4, 4),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 2, 2),
                    new LayoutMinimumSize(80, 60)),
            ]);
        return new LayoutDesignerViewModel(definition, expectedRevision: 1);
    }

    /// <summary>
    /// Dragging a panel's middle used to do nothing, so a panel could only be
    /// moved from the keyboard controls in the rail.
    /// </summary>
    [Fact]
    public void Dragging_a_panel_by_whole_cells_moves_it()
    {
        var moved = LayoutDesignerPreviewPanel.SnapMoveBounds(
            new LayoutGridBounds(0, 0, 2, 2),
            anchorColumn: 0,
            anchorRow: 0,
            currentColumn: 2,
            currentRow: 1,
            columns: 4,
            rows: 4);

        Assert.Equal(new LayoutGridBounds(2, 1, 2, 2), moved);
    }

    [Fact]
    public void A_move_cannot_push_a_panel_outside_the_grid()
    {
        var moved = LayoutDesignerPreviewPanel.SnapMoveBounds(
            new LayoutGridBounds(0, 0, 2, 2),
            anchorColumn: 0,
            anchorRow: 0,
            currentColumn: 9,
            currentRow: 9,
            columns: 4,
            rows: 4);

        Assert.Equal(new LayoutGridBounds(2, 2, 2, 2), moved);
    }

    [Fact]
    public void A_move_keeps_the_panel_size()
    {
        var original = new LayoutGridBounds(1, 1, 3, 1);

        var moved = LayoutDesignerPreviewPanel.SnapMoveBounds(
            original,
            anchorColumn: 1,
            anchorRow: 1,
            currentColumn: 0,
            currentRow: 3,
            columns: 4,
            rows: 4);

        Assert.Equal(original.ColumnSpan, moved.ColumnSpan);
        Assert.Equal(original.RowSpan, moved.RowSpan);
        Assert.Equal(new LayoutGridBounds(0, 3, 3, 1), moved);
    }

    [Fact]
    public void A_moved_panel_commits_through_the_editor()
    {
        var editor = TwoPanelsSideBySide();
        var slot = editor.Slots[0];
        var moved = LayoutDesignerPreviewPanel.SnapMoveBounds(
            slot.Bounds,
            0,
            0,
            2,
            2,
            editor.Columns,
            editor.Rows);

        var result = editor.ReplaceSlotBounds(slot.Id, slot.Bounds, moved);

        Assert.True(result.IsSuccess, result.Issue?.Message);
        Assert.Equal(new LayoutGridBounds(2, 2, 2, 2), editor.Slots[0].Bounds);
    }
}

/// <summary>
/// Every geometry operation used to assert that a panel was selected, and threw
/// when one was not — taking the window down instead of declining the edit.
/// </summary>
public sealed class LayoutDesignerRobustnessTests
{
    private static LayoutDesignerViewModel WithoutSelection()
    {
        var editor = LayoutDesignerViewModel.CreateNew();

        // Reaches the state the operations have to survive without asserting a
        // private invariant from the outside.
        typeof(LayoutDesignerViewModel)
            .GetField("_selectedSlotId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, null);
        return editor;
    }

    public static TheoryData<string> Operations => new()
    {
        "SelectNextSlot",
        "SelectPreviousSlot",
        "RemoveSelectedSlot",
        "MoveSelectedEarlier",
        "MoveSelectedLater",
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void An_operation_without_a_selection_declines_rather_than_throwing(string operation)
    {
        var editor = WithoutSelection();
        var method = typeof(LayoutDesignerViewModel).GetMethod(operation, Type.EmptyTypes)!;

        var result = (LayoutDesignerOperationResult)method.Invoke(editor, null)!;

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Moving_and_resizing_without_a_selection_decline_too()
    {
        var editor = WithoutSelection();

        Assert.False(editor.MoveSelected(LayoutDesignerDirection.Left).IsSuccess);
        Assert.False(
            editor.ResizeSelected(LayoutDesignerEdge.Right, 1).IsSuccess);
    }

    /// <summary>
    /// A layout with no panels cannot state a minimum canvas. Asking used to
    /// throw on the empty sequence.
    /// </summary>
    [Fact]
    public void An_empty_layout_reports_no_minimum_canvas_instead_of_throwing()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        typeof(LayoutDesignerViewModel)
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)
            .As<List<LayoutSlotDefinition>>()
            .Clear();

        Assert.Equal(0, editor.MinimumCanvasWidth);
        Assert.Equal(0, editor.MinimumCanvasHeight);
    }
}

internal static class ReflectionCastExtensions
{
    public static T As<T>(this object? value) => (T)value!;
}

public sealed class LayoutDesignerEdgeZoneTests
{
    [Fact]
    public void A_panel_edge_is_grabbable_from_a_practical_distance()
    {
        var panel = new Rect(0, 0, 280, 162);

        Assert.Equal(
            LayoutDesignerEdge.Left,
            LayoutDesignerPreviewPanel.HitTestEdge(panel, new Point(11, 80)));
        Assert.Equal(
            LayoutDesignerEdge.Top,
            LayoutDesignerPreviewPanel.HitTestEdge(panel, new Point(140, 11)));
    }

    /// <summary>
    /// The middle of a panel is what moves it, so the edge zone must not swallow
    /// the whole of a small one.
    /// </summary>
    [Fact]
    public void A_small_panels_middle_is_still_its_own()
    {
        var oneCell = new Rect(0, 0, 46, 40);

        Assert.Null(LayoutDesignerPreviewPanel.HitTestEdge(oneCell, new Point(23, 20)));
    }

    [Fact]
    public void The_centre_of_a_large_panel_is_never_an_edge()
    {
        var panel = new Rect(0, 0, 280, 162);

        Assert.Null(LayoutDesignerPreviewPanel.HitTestEdge(panel, new Point(140, 80)));
    }
}
