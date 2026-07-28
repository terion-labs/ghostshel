using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class LayoutDesignerViewModelTests
{
    [Fact]
    public void Existing_layout_starts_clean_and_preserves_save_identity()
    {
        var definition = TwoByTwoLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 17);

        var request = editor.CreateSaveRequest();

        Assert.False(editor.IsNew);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanSave);
        Assert.Equal(LayoutDesignerCancelDisposition.Close, editor.RequestCancel());
        Assert.Equal(definition.Id, request.Definition.Id);
        Assert.Equal(definition.SchemaVersion, request.Definition.SchemaVersion);
        Assert.Equal(17, request.ExpectedRevision);
    }

    [Fact]
    public void New_layout_is_unsaved_even_before_the_first_edit()
    {
        var editor = LayoutDesignerViewModel.CreateNew();

        Assert.True(editor.IsNew);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        Assert.Equal("UNSAVED NEW LAYOUT", editor.DirtyStatus);
        Assert.Equal(
            LayoutDesignerCancelDisposition.ConfirmDiscard,
            editor.RequestCancel());
    }

    [Fact]
    public void Keyboard_selection_wraps_in_accessibility_order_without_dirtying()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1);

        Assert.Equal("top-left", editor.SelectedSlotId?.Value);
        Assert.True(editor.SelectPreviousSlot().IsSuccess);
        Assert.Equal("bottom", editor.SelectedSlotId?.Value);
        Assert.True(editor.SelectNextSlot().IsSuccess);
        Assert.Equal("top-left", editor.SelectedSlotId?.Value);
        Assert.False(editor.IsDirty);
        Assert.Equal([1, 2, 3], editor.Slots.Select(slot => slot.Order));
    }

    [Fact]
    public void Move_rejects_overlap_and_out_of_bounds_without_mutating_geometry()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1);
        var original = editor.SelectedSlot!.Bounds;

        var overlap = editor.MoveSelected(LayoutDesignerDirection.Right);
        Assert.True(editor.SelectSlot(new LayoutSlotId("top-right")).IsSuccess);
        var outside = editor.MoveSelected(LayoutDesignerDirection.Right);

        Assert.False(overlap.IsSuccess);
        Assert.Equal(DefinitionValidationCode.Overlap, overlap.Issue?.Code);
        Assert.False(outside.IsSuccess);
        Assert.Equal(DefinitionValidationCode.OutOfBounds, outside.Issue?.Code);
        Assert.True(editor.SelectSlot(new LayoutSlotId("top-left")).IsSuccess);
        Assert.Equal(original, editor.SelectedSlot!.Bounds);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Move_applies_a_valid_keyboard_grid_step()
    {
        var editor = new LayoutDesignerViewModel(LayoutWithMoveSpace(), expectedRevision: 1);

        var result = editor.MoveSelected(LayoutDesignerDirection.Right);

        Assert.True(result.IsSuccess);
        Assert.Equal(new LayoutGridBounds(1, 0, 1, 1), editor.SelectedSlot!.Bounds);
        Assert.True(editor.IsDirty);
        Assert.Equal(
            LayoutDesignerCancelDisposition.ConfirmDiscard,
            editor.RequestCancel());
    }

    [Fact]
    public void Resize_moves_named_edges_and_rejects_zero_span()
    {
        var definition = new LayoutDefinition(
            new LayoutId("resizable"),
            LayoutDefinition.CurrentSchemaVersion,
            "Resizable",
            new LayoutGrid(4, 2),
            [Slot("main", 1, 0, 2, 2)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        Assert.True(editor.ResizeSelected(LayoutDesignerEdge.Left, 1).IsSuccess);
        Assert.Equal(new LayoutGridBounds(0, 0, 3, 2), editor.SelectedSlot!.Bounds);
        Assert.True(editor.ResizeSelected(LayoutDesignerEdge.Left, -1).IsSuccess);
        Assert.Equal(new LayoutGridBounds(1, 0, 2, 2), editor.SelectedSlot!.Bounds);
        Assert.True(editor.ResizeSelected(LayoutDesignerEdge.Right, -1).IsSuccess);

        var zeroSpan = editor.ResizeSelected(LayoutDesignerEdge.Right, -1);

        Assert.False(zeroSpan.IsSuccess);
        Assert.Equal(DefinitionValidationCode.InvalidBounds, zeroSpan.Issue?.Code);
        Assert.Equal(new LayoutGridBounds(1, 0, 1, 2), editor.SelectedSlot!.Bounds);
    }

    [Theory]
    [InlineData((int)LayoutDesignerEdge.Left, 0, 1, 2, 1)]
    [InlineData((int)LayoutDesignerEdge.Right, 1, 1, 2, 1)]
    [InlineData((int)LayoutDesignerEdge.Top, 1, 0, 1, 2)]
    [InlineData((int)LayoutDesignerEdge.Bottom, 1, 1, 1, 2)]
    public void Every_edge_can_expand_by_a_keyboard_grid_step(
        int edge,
        int expectedColumn,
        int expectedRow,
        int expectedColumnSpan,
        int expectedRowSpan)
    {
        var definition = new LayoutDefinition(
            new LayoutId("all-edges"),
            LayoutDefinition.CurrentSchemaVersion,
            "All edges",
            new LayoutGrid(3, 3),
            [Slot("main", 1, 1, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        var result = editor.ResizeSelected((LayoutDesignerEdge)edge, 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new LayoutGridBounds(
                expectedColumn,
                expectedRow,
                expectedColumnSpan,
                expectedRowSpan),
            editor.SelectedSlot!.Bounds);
    }

    [Fact]
    public void Resize_rejects_overlap_without_partially_applying_it()
    {
        var definition = new LayoutDefinition(
            new LayoutId("resize-overlap"),
            LayoutDefinition.CurrentSchemaVersion,
            "Resize overlap",
            new LayoutGrid(3, 1),
            [Slot("left", 0, 0, 1, 1), Slot("right", 2, 0, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        var firstGrowth = editor.ResizeSelected(LayoutDesignerEdge.Right, 1);
        var overlappingGrowth = editor.ResizeSelected(LayoutDesignerEdge.Right, 1);

        Assert.True(firstGrowth.IsSuccess);
        Assert.False(overlappingGrowth.IsSuccess);
        Assert.Equal(DefinitionValidationCode.Overlap, overlappingGrowth.Issue?.Code);
        Assert.Equal(new LayoutGridBounds(0, 0, 2, 1), editor.SelectedSlot!.Bounds);
    }

    [Fact]
    public void Pointer_resize_commits_the_exact_replacement_bounds()
    {
        var editor = new LayoutDesignerViewModel(LayoutWithMoveSpace(), expectedRevision: 1);
        var slotId = editor.SelectedSlot!.Id;
        var original = editor.SelectedSlot.Bounds;
        var replacement = new LayoutGridBounds(0, 0, 2, 1);

        var result = editor.ReplaceSlotBounds(slotId, original, replacement);

        Assert.True(result.IsSuccess);
        Assert.Equal(replacement, editor.SelectedSlot!.Bounds);
    }

    [Fact]
    public void Stale_pointer_resize_preserves_a_newer_keyboard_edit()
    {
        var definition = new LayoutDefinition(
            new LayoutId("concurrent-resize"),
            LayoutDefinition.CurrentSchemaVersion,
            "Concurrent resize",
            new LayoutGrid(4, 1),
            [Slot("main", 0, 0, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);
        var slotId = editor.SelectedSlot!.Id;
        var pointerStart = editor.SelectedSlot.Bounds;
        Assert.True(editor.ResizeSelected(LayoutDesignerEdge.Right, 1).IsSuccess);
        var keyboardBounds = editor.SelectedSlot.Bounds;

        var result = editor.ReplaceSlotBounds(
            slotId,
            pointerStart,
            new LayoutGridBounds(0, 0, 3, 1));

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionValidationCode.InvalidBounds, result.Issue?.Code);
        Assert.Equal(keyboardBounds, editor.SelectedSlot!.Bounds);
    }

    [Fact]
    public void Grid_shrink_rejects_clipped_panels_and_expansion_updates_canvas_minimum()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1);
        var originalGrid = editor.Grid;

        var clipped = editor.ResizeGrid(rows: 1, columns: 2);

        Assert.False(clipped.IsSuccess);
        Assert.Equal(DefinitionValidationCode.OutOfBounds, clipped.Issue?.Code);
        Assert.Equal(originalGrid, editor.Grid);

        var expanded = editor.ResizeGrid(rows: 2, columns: 3);

        Assert.True(expanded.IsSuccess);
        Assert.Equal(660, editor.MinimumCanvasWidth);
        Assert.Equal(280, editor.MinimumCanvasHeight);
    }

    [Fact]
    public void Minimum_size_edit_is_validated_and_drives_canvas_validation()
    {
        var definition = new LayoutDefinition(
            new LayoutId("minimum"),
            LayoutDefinition.CurrentSchemaVersion,
            "Minimum",
            new LayoutGrid(1, 1),
            [Slot("main", 0, 0, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        var invalid = editor.SetSelectedMinimumSize(new LayoutMinimumSize(double.NaN, 100));
        var valid = editor.SetSelectedMinimumSize(new LayoutMinimumSize(400, 300));

        Assert.False(invalid.IsSuccess);
        Assert.Equal(DefinitionValidationCode.InvalidMinimumSize, invalid.Issue?.Code);
        Assert.True(valid.IsSuccess);
        Assert.Equal(400, editor.MinimumCanvasWidth);
        Assert.Equal(300, editor.MinimumCanvasHeight);
        var canvasValidation = editor.ValidateCanvas(new LayoutCanvasSize(399, 300));
        Assert.Contains(
            canvasValidation.Issues,
            issue => issue.Code == DefinitionValidationCode.CanvasTooSmall);
    }

    [Fact]
    public void Panel_order_changes_save_order_and_controls_keyboard_traversal()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1);

        Assert.True(editor.MoveSelectedLater().IsSuccess);

        Assert.Equal(
            ["top-right", "top-left", "bottom"],
            editor.Slots.Select(slot => slot.Id.Value));
        Assert.True(editor.CanMoveSelectedEarlier);
        Assert.True(editor.CanMoveSelectedLater);
        Assert.Equal(
            ["top-right", "top-left", "bottom"],
            editor.CreateSaveRequest().Definition.Slots.Select(slot => slot.Id.Value));
    }

    [Fact]
    public void Reorder_at_boundary_is_rejected_without_marking_clean_editor_dirty()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1);

        var result = editor.MoveSelectedEarlier();

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionValidationCode.InvalidBounds, result.Issue?.Code);
        Assert.True(editor.HasOperationError);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Add_and_remove_use_empty_cells_and_keep_a_valid_selection()
    {
        var editor = new LayoutDesignerViewModel(LayoutWithMoveSpace(), expectedRevision: 1);

        var added = editor.AddSlot();

        Assert.True(added.IsSuccess);
        Assert.Equal(2, editor.Slots.Count);
        Assert.Equal(new LayoutGridBounds(1, 0, 1, 1), editor.SelectedSlot!.Bounds);
        Assert.True(editor.RemoveSelectedSlot().IsSuccess);
        Assert.Single(editor.Slots);
        Assert.NotNull(editor.SelectedSlot);

        var lastRemoval = editor.RemoveSelectedSlot();

        Assert.False(lastRemoval.IsSuccess);
        Assert.Equal(DefinitionValidationCode.Required, lastRemoval.Issue?.Code);
    }

    /// <summary>
    /// Painting used to be gated behind a mode the user had to arm first, while
    /// the canvas told them to drag across empty cells. Following the printed
    /// instruction did nothing, so the grid read as broken.
    /// </summary>
    [Fact]
    public void Painting_a_region_needs_no_mode_to_be_armed_first()
    {
        var definition = new LayoutDefinition(
            new LayoutId("paint"),
            LayoutDefinition.CurrentSchemaVersion,
            "Paint",
            new LayoutGrid(4, 3),
            [Slot("existing", 0, 0, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        var result = editor.AddSlot(new LayoutGridBounds(1, 1, 3, 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(new LayoutGridBounds(1, 1, 3, 2), editor.SelectedSlot!.Bounds);
        Assert.Equal(2, editor.Slots.Count);
    }

    [Fact]
    public void Painting_over_an_existing_panel_is_rejected_without_mutation()
    {
        var editor = new LayoutDesignerViewModel(LayoutWithMoveSpace(), expectedRevision: 1);
        var original = editor.CreateSaveRequest().Definition.Slots;

        var result = editor.AddSlot(new LayoutGridBounds(0, 0, 2, 1));

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionValidationCode.Overlap, result.Issue?.Code);
        Assert.Equal(original, editor.CreateSaveRequest().Definition.Slots);
    }

    [Fact]
    public void A_full_grid_refuses_another_panel_without_dirtying_the_layout()
    {
        var definition = new LayoutDefinition(
            new LayoutId("full"),
            LayoutDefinition.CurrentSchemaVersion,
            "Full",
            new LayoutGrid(1, 1),
            [Slot("only", 0, 0, 1, 1)]);
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        var result = editor.AddSlot();

        Assert.False(result.IsSuccess);
        Assert.False(editor.IsDirty);
    }

    /// <summary>
    /// Grid coordinates say where a panel is anchored, not how big it will look.
    /// The canvas shows the panel's share of the grid instead, which is the thing
    /// being designed.
    /// </summary>
    [Theory]
    [InlineData(6, 4, "½ × ½")]
    [InlineData(12, 8, "1 × 1")]
    [InlineData(3, 2, "¼ × ¼")]
    [InlineData(4, 8, "⅓ × 1")]
    public void A_panel_reports_its_share_of_the_grid(
        int columnSpan,
        int rowSpan,
        string expected)
    {
        var definition = new LayoutDefinition(
            new LayoutId("share"),
            LayoutDefinition.CurrentSchemaVersion,
            "Share",
            new LayoutGrid(12, 8),
            [Slot("only", 0, 0, columnSpan, rowSpan)]);

        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        Assert.Equal(expected, editor.Slots[0].SizeLabel);
    }

    /// <summary>
    /// A ratio with no familiar glyph is shown as a plain ratio rather than
    /// rounded to the nearest one, which would state a size the panel is not.
    /// </summary>
    [Fact]
    public void An_unusual_share_is_stated_exactly_rather_than_approximated()
    {
        var definition = new LayoutDefinition(
            new LayoutId("odd"),
            LayoutDefinition.CurrentSchemaVersion,
            "Odd",
            new LayoutGrid(7, 8),
            [Slot("only", 0, 0, 2, 8)]);

        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        Assert.Equal("2/7 × 1", editor.Slots[0].SizeLabel);
    }

    [Fact]
    public void Panel_palette_is_stable_and_cycles_after_four_slots()
    {
        var first = new LayoutDesignerSlotViewModel(
            1,
            Slot("one", 0, 0, 1, 1),
            IsSelected: true);
        var second = first with { Order = 2 };
        var third = first with { Order = 3 };
        var fourth = first with { Order = 4 };
        var fifth = first with { Order = 5 };

        Assert.True(first.UsesOrangePalette);
        Assert.True(second.UsesBluePalette);
        Assert.True(third.UsesGreenPalette);
        Assert.True(fourth.UsesPinkPalette);
        Assert.True(fifth.UsesOrangePalette);
    }

    [Fact]
    public void Reset_restores_name_grid_geometry_order_and_clean_state()
    {
        var definition = TwoByTwoLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 9)
        {
            Name = "Changed",
        };
        Assert.True(editor.MoveSelectedLater().IsSuccess);
        Assert.True(editor.ResizeGrid(rows: 3, columns: 2).IsSuccess);
        Assert.True(editor.SelectSlot(new LayoutSlotId("bottom")).IsSuccess);
        Assert.True(editor.MoveSelected(LayoutDesignerDirection.Down).IsSuccess);

        editor.Reset();

        Assert.Equal(definition.Name, editor.Name);
        Assert.Equal(definition.Grid, editor.Grid);
        Assert.Equal(definition.Slots, editor.CreateSaveRequest().Definition.Slots);
        Assert.Equal(definition.Slots[0].Id, editor.SelectedSlotId);
        Assert.False(editor.IsDirty);
        Assert.Equal(LayoutDesignerCancelDisposition.Close, editor.RequestCancel());
    }

    [Fact]
    public void Invalid_name_is_reported_live_and_rejected_before_persistence()
    {
        var editor = new LayoutDesignerViewModel(TwoByTwoLayout(), expectedRevision: 1)
        {
            Name = "   ",
        };

        Assert.False(editor.IsValid);
        Assert.False(editor.CanSave);
        Assert.Contains(
            editor.ValidationIssues,
            issue => issue.Code == DefinitionValidationCode.Required);
        var exception = Assert.Throws<InvalidOperationException>(editor.CreateSaveRequest);
        Assert.Contains("name is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LayoutDefinition TwoByTwoLayout() => new(
        new LayoutId("two-by-two"),
        LayoutDefinition.CurrentSchemaVersion,
        "Two by two",
        new LayoutGrid(2, 2),
        [
            Slot("top-left", 0, 0, 1, 1),
            Slot("top-right", 1, 0, 1, 1),
            Slot("bottom", 0, 1, 2, 1),
        ]);

    private static LayoutDefinition LayoutWithMoveSpace() => new(
        new LayoutId("move-space"),
        LayoutDefinition.CurrentSchemaVersion,
        "Move space",
        new LayoutGrid(2, 1),
        [Slot("main", 0, 0, 1, 1)]);

    private static LayoutSlotDefinition Slot(
        string id,
        int column,
        int row,
        int columnSpan,
        int rowSpan) =>
        new(
            new LayoutSlotId(id),
            new LayoutGridBounds(column, row, columnSpan, rowSpan),
            new LayoutMinimumSize(
                LayoutDesignerViewModel.DefaultPanelMinimumWidth,
                LayoutDesignerViewModel.DefaultPanelMinimumHeight));
}
