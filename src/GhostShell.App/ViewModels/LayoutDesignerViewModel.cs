using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns an isolated, transactional layout edit. Geometry operations validate a complete
/// candidate before publishing it, so a rejected keyboard action never leaves partial state.
/// Instances are UI-owned and are not thread-safe.
/// </summary>
public sealed class LayoutDesignerViewModel : ObservableObject
{
    public const double DefaultPanelMinimumWidth = 220;
    public const double DefaultPanelMinimumHeight = 140;

    private readonly LayoutDefinition _original;
    private readonly List<LayoutSlotDefinition> _slots;
    private LayoutGrid _grid;
    private string _name;
    private LayoutSlotId? _selectedSlotId;
    private IReadOnlyList<LayoutDesignerSlotViewModel> _slotSnapshots = [];
    private IReadOnlyList<DefinitionValidationIssue> _validationIssues = [];
    private DefinitionValidationIssue? _lastOperationIssue;
    private LayoutGridBounds? _paintPreviewBounds;
    private bool _isDirty;

    public LayoutDesignerViewModel(
        LayoutDefinition definition,
        long? expectedRevision)
    {
        _original = definition ?? throw new ArgumentNullException(nameof(definition));
        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The source layout is invalid: {FormatIssues(validation.Issues)}",
                nameof(definition));
        }

        ExpectedRevision = expectedRevision;
        _name = definition.Name;
        _grid = definition.Grid;
        _slots = [.. definition.Slots];
        _selectedSlotId = _slots[0].Id;
        PublishState();
    }

    public static LayoutDesignerViewModel CreateNew(string name = "Untitled layout")
    {
        var definition = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(2, 1),
            [
                new(
                    new LayoutSlotId("slot-1-1"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(
                        DefaultPanelMinimumWidth,
                        DefaultPanelMinimumHeight)),
                new(
                    new LayoutSlotId("slot-1-2"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(
                        DefaultPanelMinimumWidth,
                        DefaultPanelMinimumHeight)),
            ]);
        return new(definition, expectedRevision: null);
    }

    public LayoutId Id => _original.Id;

    public int SchemaVersion => _original.SchemaVersion;

    public long? ExpectedRevision { get; }

    public bool IsNew => ExpectedRevision is null;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                PublishState();
            }
        }
    }

    public int Rows => _grid.Rows;

    public int Columns => _grid.Columns;

    public LayoutGrid Grid => _grid;

    public string GridSummary =>
        $"{Columns} × {Rows} grid · {_slotSnapshots.Count} panels";

    /// <summary>Slots are exposed in their keyboard and accessibility traversal order.</summary>
    public IReadOnlyList<LayoutDesignerSlotViewModel> Slots => _slotSnapshots;

    /// <summary>
    /// The panel count as its own property. Binding to <c>Slots.Count</c> would
    /// silently render nothing: the snapshot is an array, whose only public
    /// length member is <c>Length</c>, and the binding resolver reflects over the
    /// runtime type rather than the declared interface.
    /// </summary>
    public int PanelCount => _slotSnapshots.Count;

    public LayoutSlotId? SelectedSlotId => _selectedSlotId;

    public LayoutDesignerSlotViewModel? SelectedSlot => _slotSnapshots
        .SingleOrDefault(slot => slot.IsSelected);

    public bool CanMoveSelectedEarlier => SelectedSlot is { Order: > 1 };

    public bool CanMoveSelectedLater => SelectedSlot is { Order: var order }
        && order < _slotSnapshots.Count;

    public bool IsDirty => _isDirty;

    public string DirtyStatus => IsNew
        ? "UNSAVED NEW LAYOUT"
        : IsDirty
            ? "UNSAVED CHANGES"
            : "SAVED DEFINITION";

    public IReadOnlyList<DefinitionValidationIssue> ValidationIssues => _validationIssues;

    public bool IsValid => ValidationIssues.Count == 0;

    public bool CanSave => IsDirty && IsValid;

    public string ValidationSummary => IsValid
        ? "Layout is valid."
        : FormatIssues(ValidationIssues);

    /// <summary>
    /// Smallest uniform-grid canvas width that can satisfy every slot minimum.
    /// </summary>
    public double MinimumCanvasWidth => _slots.Count == 0
        ? 0
        : _slots.Max(slot => slot.MinimumSize.Width * Columns / slot.Bounds.ColumnSpan);

    /// <summary>
    /// Smallest uniform-grid canvas height that can satisfy every slot minimum.
    /// </summary>
    public double MinimumCanvasHeight => _slots.Count == 0
        ? 0
        : _slots.Max(slot => slot.MinimumSize.Height * Rows / slot.Bounds.RowSpan);

    public DefinitionValidationIssue? LastOperationIssue => _lastOperationIssue;

    public bool HasOperationError => LastOperationIssue is not null;

    /// <summary>
    /// What the grid does right now, stated where the user is looking. There is no
    /// painting mode to describe: dragging an empty cell always paints.
    /// </summary>
    public string GridHint =>
        "Drag empty cells to paint · drag a panel to move it · drag its edge to resize";

    public LayoutGridBounds? PaintPreviewBounds => _paintPreviewBounds;

    // Selection and geometry operations form the keyboard-editing surface. The view maps
    // gestures to these intent-level methods and never mutates slot coordinates directly.

    public LayoutDesignerOperationResult SelectSlot(LayoutSlotId id)
    {
        if (_slots.All(slot => slot.Id != id))
        {
            return Reject(
                DefinitionValidationCode.UnknownSlot,
                $"Layout slot '{id}' does not exist.",
                id.Value);
        }

        _selectedSlotId = id;
        ClearOperationIssue();
        PublishSelection();
        return LayoutDesignerOperationResult.Applied;
    }

    public LayoutDesignerOperationResult SelectNextSlot()
    {
        var currentIndex = SelectedIndex();
        if (currentIndex < 0 || _slots.Count == 0)
        {
            return NoSelection();
        }

        var nextIndex = (currentIndex + 1) % _slots.Count;
        return SelectSlot(_slots[nextIndex].Id);
    }

    public LayoutDesignerOperationResult SelectPreviousSlot()
    {
        var currentIndex = SelectedIndex();
        if (currentIndex < 0 || _slots.Count == 0)
        {
            return NoSelection();
        }

        var previousIndex = (currentIndex - 1 + _slots.Count) % _slots.Count;
        return SelectSlot(_slots[previousIndex].Id);
    }

    public LayoutDesignerOperationResult MoveSelected(LayoutDesignerDirection direction)
    {
        var selectedIndex = SelectedIndex();
        if (selectedIndex < 0)
        {
            return NoSelection();
        }

        var selected = _slots[selectedIndex];
        var bounds = selected.Bounds;
        var moved = direction switch
        {
            LayoutDesignerDirection.Left => bounds with { Column = bounds.Column - 1 },
            LayoutDesignerDirection.Right => bounds with { Column = bounds.Column + 1 },
            LayoutDesignerDirection.Up => bounds with { Row = bounds.Row - 1 },
            LayoutDesignerDirection.Down => bounds with { Row = bounds.Row + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        return ApplySlotChange(selectedIndex, selected with { Bounds = moved });
    }

    /// <summary>
    /// Moves one edge by logical grid units. Positive values expand toward the named edge;
    /// negative values contract that edge toward the slot interior.
    /// </summary>
    public LayoutDesignerOperationResult ResizeSelected(
        LayoutDesignerEdge edge,
        int gridUnits)
    {
        if (gridUnits == 0)
        {
            return Reject(
                DefinitionValidationCode.InvalidBounds,
                "A resize must move the selected edge by at least one grid unit.",
                _selectedSlotId?.Value);
        }

        var selectedIndex = SelectedIndex();
        if (selectedIndex < 0)
        {
            return NoSelection();
        }

        var selected = _slots[selectedIndex];
        var bounds = selected.Bounds;
        var resized = edge switch
        {
            LayoutDesignerEdge.Left => CreateBounds(
                (long)bounds.Column - gridUnits,
                bounds.Row,
                (long)bounds.ColumnSpan + gridUnits,
                bounds.RowSpan),
            LayoutDesignerEdge.Right => CreateBounds(
                bounds.Column,
                bounds.Row,
                (long)bounds.ColumnSpan + gridUnits,
                bounds.RowSpan),
            LayoutDesignerEdge.Top => CreateBounds(
                bounds.Column,
                (long)bounds.Row - gridUnits,
                bounds.ColumnSpan,
                (long)bounds.RowSpan + gridUnits),
            LayoutDesignerEdge.Bottom => CreateBounds(
                bounds.Column,
                bounds.Row,
                bounds.ColumnSpan,
                (long)bounds.RowSpan + gridUnits),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        if (resized is null)
        {
            return Reject(
                DefinitionValidationCode.InvalidBounds,
                "The requested resize exceeds the supported grid coordinate range.",
                selected.Id.Value);
        }

        return ApplySlotChange(selectedIndex, selected with { Bounds = resized });
    }

    /// <summary>
    /// Commits the exact pointer-resize preview only when the slot still has the geometry
    /// captured at gesture start. Keyboard or other concurrent edits therefore win instead
    /// of having a stale pointer delta applied to newer geometry.
    /// </summary>
    public LayoutDesignerOperationResult ReplaceSlotBounds(
        LayoutSlotId id,
        LayoutGridBounds expectedBounds,
        LayoutGridBounds replacementBounds)
    {
        ArgumentNullException.ThrowIfNull(expectedBounds);
        ArgumentNullException.ThrowIfNull(replacementBounds);
        var selectedIndex = _slots.FindIndex(slot => slot.Id == id);
        if (selectedIndex < 0)
        {
            return Reject(
                DefinitionValidationCode.UnknownSlot,
                $"Layout slot '{id}' does not exist.",
                id.Value);
        }

        var selected = _slots[selectedIndex];
        if (selected.Bounds != expectedBounds)
        {
            return Reject(
                DefinitionValidationCode.InvalidBounds,
                "The panel changed while it was being resized. Its newer geometry was kept.",
                id.Value);
        }

        return ApplyGeometry(
            _slots.Select(slot => slot.Id == id
                ? slot with { Bounds = replacementBounds }
                : slot).ToArray(),
            _grid,
            id);
    }

    public LayoutDesignerOperationResult ResizeGrid(int rows, int columns)
    {
        if (rows < 1 || columns < 1)
        {
            return Reject(
                DefinitionValidationCode.InvalidGrid,
                "A layout grid must have at least one row and one column.",
                Id.Value);
        }

        return ApplyGeometry(_slots, new LayoutGrid(columns, rows));
    }

    public LayoutDesignerOperationResult SetSelectedMinimumSize(LayoutMinimumSize minimumSize)
    {
        ArgumentNullException.ThrowIfNull(minimumSize);
        var selectedIndex = SelectedIndex();
        if (selectedIndex < 0)
        {
            return NoSelection();
        }

        var selected = _slots[selectedIndex];
        return ApplySlotChange(selectedIndex, selected with { MinimumSize = minimumSize });
    }

    public DefinitionValidationResult ValidateCanvas(LayoutCanvasSize canvas) =>
        LayoutArranger.Arrange(BuildDefinition(), canvas).Validation;

    public LayoutDesignerOperationResult AddSlot()
    {
        if (FindFirstEmptyCell() is { } emptyCell)
        {
            return AddSlot(new LayoutGridBounds(emptyCell.Column, emptyCell.Row, 1, 1));
        }

        // A finished layout normally covers the grid, so refusing to add a panel
        // whenever there is no gap made "add a panel" a dead end: the only way out
        // was to shrink something by hand first, and there is nothing on screen
        // that says so. Take half of the largest panel instead.
        return SplitLargestSlot();
    }

    /// <summary>
    /// Halves the largest panel and gives the freed half to a new one, splitting
    /// along its longer axis so the result stays close to square. A grid of
    /// single-cell panels has nothing left to divide, which is the one case that
    /// still has to be refused.
    /// </summary>
    private LayoutDesignerOperationResult SplitLargestSlot()
    {
        var donor = _slots
            .Where(slot => slot.Bounds.ColumnSpan > 1 || slot.Bounds.RowSpan > 1)
            .MaxBy(slot => slot.Bounds.ColumnSpan * slot.Bounds.RowSpan);
        if (donor is null)
        {
            return Reject(
                DefinitionValidationCode.OutOfBounds,
                "Every panel is already a single cell. Add rows or columns to fit another.",
                Id.Value);
        }

        var bounds = donor.Bounds;
        var splitAcrossColumns = bounds.ColumnSpan >= bounds.RowSpan;
        var keptSpan = splitAcrossColumns
            ? bounds.ColumnSpan / 2
            : bounds.RowSpan / 2;

        var keptBounds = splitAcrossColumns
            ? new LayoutGridBounds(bounds.Column, bounds.Row, keptSpan, bounds.RowSpan)
            : new LayoutGridBounds(bounds.Column, bounds.Row, bounds.ColumnSpan, keptSpan);
        var addedBounds = splitAcrossColumns
            ? new LayoutGridBounds(
                bounds.Column + keptSpan,
                bounds.Row,
                bounds.ColumnSpan - keptSpan,
                bounds.RowSpan)
            : new LayoutGridBounds(
                bounds.Column,
                bounds.Row + keptSpan,
                bounds.ColumnSpan,
                bounds.RowSpan - keptSpan);

        var id = NextSlotId();
        var candidate = _slots
            .Select(slot => slot.Id == donor.Id ? slot with { Bounds = keptBounds } : slot)
            .Append(new LayoutSlotDefinition(
                id,
                addedBounds,
                new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight)))
            .ToArray();
        return ApplyGeometry(candidate, _grid, id);
    }

    public LayoutDesignerOperationResult AddSlot(LayoutGridBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var id = NextSlotId();
        var slot = new LayoutSlotDefinition(
            id,
            bounds,
            new LayoutMinimumSize(
                DefaultPanelMinimumWidth,
                DefaultPanelMinimumHeight));
        List<LayoutSlotDefinition> candidate = [.. _slots, slot];
        var result = ApplyGeometry(candidate, _grid, id);
        return result;
    }

    public LayoutDesignerOperationResult RemoveSelectedSlot()
    {
        if (_slots.Count == 1)
        {
            return Reject(
                DefinitionValidationCode.Required,
                "A layout must contain at least one panel.",
                _selectedSlotId?.Value);
        }

        var selectedIndex = SelectedIndex();
        if (selectedIndex < 0)
        {
            return NoSelection();
        }

        List<LayoutSlotDefinition> candidate = [.. _slots];
        candidate.RemoveAt(selectedIndex);
        var nextSelection = candidate[Math.Min(selectedIndex, candidate.Count - 1)].Id;
        return ApplyGeometry(candidate, _grid, nextSelection);
    }

    public LayoutDesignerOperationResult MoveSelectedEarlier() =>
        ReorderSelected(-1);

    public LayoutDesignerOperationResult MoveSelectedLater() =>
        ReorderSelected(1);

    internal void SetPaintPreviewBounds(LayoutGridBounds? bounds)
    {
        if (_paintPreviewBounds == bounds)
        {
            return;
        }

        _paintPreviewBounds = bounds;
        OnPropertyChanged(nameof(PaintPreviewBounds));
    }

    public void Reset()
    {
        var nameChanged = !StringComparer.Ordinal.Equals(_name, _original.Name);
        _name = _original.Name;
        _grid = _original.Grid;
        _slots.Clear();
        _slots.AddRange(_original.Slots);
        _selectedSlotId = _slots.Count > 0 ? _slots[0].Id : null;
        ClearOperationIssue();
        if (nameChanged)
        {
            OnPropertyChanged(nameof(Name));
        }

        PublishState();
    }

    public LayoutDesignerCancelDisposition RequestCancel() => IsDirty
        ? LayoutDesignerCancelDisposition.ConfirmDiscard
        : LayoutDesignerCancelDisposition.Close;

    public LayoutDesignerSaveRequest CreateSaveRequest()
    {
        var definition = BuildDefinition();
        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"The layout cannot be saved: {FormatIssues(validation.Issues)}");
        }

        definition = new LayoutDefinition(
            definition.Id,
            definition.SchemaVersion,
            definition.Name,
            definition.Grid,
            definition.Slots,
            RuntimeDockLayoutController.SerializeDefinition(definition));

        return new(definition, ExpectedRevision);
    }

    // Candidate commit and state projection. Every geometry change crosses this boundary,
    // where the complete layout is validated before any observable state changes.

    private LayoutDesignerOperationResult ApplySlotChange(
        int selectedIndex,
        LayoutSlotDefinition replacement)
    {
        List<LayoutSlotDefinition> candidate = [.. _slots];
        candidate[selectedIndex] = replacement;
        return ApplyGeometry(candidate, _grid);
    }

    private LayoutDesignerOperationResult ReorderSelected(int offset)
    {
        var selectedIndex = SelectedIndex();
        if (selectedIndex < 0)
        {
            return NoSelection();
        }

        var destination = selectedIndex + offset;
        if (destination < 0 || destination >= _slots.Count)
        {
            return Reject(
                DefinitionValidationCode.InvalidBounds,
                offset < 0
                    ? "The selected panel is already first in traversal order."
                    : "The selected panel is already last in traversal order.",
                _selectedSlotId?.Value);
        }

        List<LayoutSlotDefinition> candidate = [.. _slots];
        var selected = candidate[selectedIndex];
        candidate.RemoveAt(selectedIndex);
        candidate.Insert(destination, selected);
        return ApplyGeometry(candidate, _grid);
    }

    private LayoutDesignerOperationResult ApplyGeometry(
        IReadOnlyList<LayoutSlotDefinition> candidateSlots,
        LayoutGrid candidateGrid,
        LayoutSlotId? nextSelection = null)
    {
        // Some callers intentionally pass the live list when only the grid changes.
        // Snapshot before committing so clearing the old state cannot clear the candidate.
        var committedSlots = candidateSlots.ToArray();
        var candidate = BuildDefinition(candidateGrid, committedSlots);
        var issue = LayoutValidator.Validate(candidate).Issues
            .FirstOrDefault(candidateIssue => candidateIssue.Code != DefinitionValidationCode.Required);
        if (issue is not null)
        {
            return Reject(issue);
        }

        _grid = candidateGrid;
        _slots.Clear();
        _slots.AddRange(committedSlots);
        _selectedSlotId = nextSelection ?? _selectedSlotId;
        ClearOperationIssue();
        PublishState();
        return LayoutDesignerOperationResult.Applied;
    }

    private (int Column, int Row)? FindFirstEmptyCell()
    {
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var occupied = _slots.Any(slot =>
                    column >= slot.Bounds.Column
                    && column < slot.Bounds.Column + slot.Bounds.ColumnSpan
                    && row >= slot.Bounds.Row
                    && row < slot.Bounds.Row + slot.Bounds.RowSpan);
                if (!occupied)
                {
                    return (column, row);
                }
            }
        }

        return null;
    }

    private LayoutSlotId NextSlotId()
    {
        var suffix = _slots.Count + 1;
        while (_slots.Any(slot =>
                   StringComparer.Ordinal.Equals(slot.Id.Value, $"slot-{suffix}")))
        {
            suffix++;
        }

        return new($"slot-{suffix}");
    }

    /// <summary>
    /// The selected panel's position, or -1 when there is none. This used to
    /// throw, which turned a lost selection into a crash of the whole window
    /// rather than an operation that declines to run.
    /// </summary>
    private int SelectedIndex() =>
        _slots.FindIndex(slot => slot.Id == _selectedSlotId);

    private LayoutDesignerOperationResult NoSelection() =>
        Reject(
            DefinitionValidationCode.UnknownSlot,
            "Select a panel first.",
            _selectedSlotId?.Value);

    private LayoutDefinition BuildDefinition() => BuildDefinition(_grid, _slots);

    private LayoutDefinition BuildDefinition(
        LayoutGrid grid,
        IReadOnlyList<LayoutSlotDefinition> slots) =>
        new(Id, SchemaVersion, Name, grid, slots);

    private bool MatchesOriginal() =>
        StringComparer.Ordinal.Equals(Name, _original.Name)
        && Grid == _original.Grid
        && _slots.SequenceEqual(_original.Slots);

    private void PublishState()
    {
        _slotSnapshots = _slots
            .Select((slot, index) => new LayoutDesignerSlotViewModel(
                index + 1,
                slot,
                slot.Id == _selectedSlotId,
                Columns,
                Rows))
            .ToArray();
        _validationIssues = LayoutValidator.Validate(BuildDefinition()).Issues;
        _isDirty = IsNew || !MatchesOriginal();

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Grid));
        OnPropertyChanged(nameof(GridSummary));
        OnPropertyChanged(nameof(Slots));
        OnPropertyChanged(nameof(SelectedSlotId));
        OnPropertyChanged(nameof(SelectedSlot));
        OnPropertyChanged(nameof(CanMoveSelectedEarlier));
        OnPropertyChanged(nameof(CanMoveSelectedLater));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtyStatus));
        OnPropertyChanged(nameof(ValidationIssues));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(MinimumCanvasWidth));
        OnPropertyChanged(nameof(MinimumCanvasHeight));
    }

    private void PublishSelection()
    {
        _slotSnapshots = _slots
            .Select((slot, index) => new LayoutDesignerSlotViewModel(
                index + 1,
                slot,
                slot.Id == _selectedSlotId,
                Columns,
                Rows))
            .ToArray();
        OnPropertyChanged(nameof(Slots));
        OnPropertyChanged(nameof(SelectedSlotId));
        OnPropertyChanged(nameof(SelectedSlot));
        OnPropertyChanged(nameof(CanMoveSelectedEarlier));
        OnPropertyChanged(nameof(CanMoveSelectedLater));
    }

    private void ClearOperationIssue()
    {
        if (_lastOperationIssue is null)
        {
            return;
        }

        _lastOperationIssue = null;
        OnPropertyChanged(nameof(LastOperationIssue));
        OnPropertyChanged(nameof(HasOperationError));
    }

    private LayoutDesignerOperationResult Reject(DefinitionValidationIssue issue)
    {
        _lastOperationIssue = issue;
        OnPropertyChanged(nameof(LastOperationIssue));
        OnPropertyChanged(nameof(HasOperationError));
        return LayoutDesignerOperationResult.Rejected(issue);
    }

    private LayoutDesignerOperationResult Reject(
        DefinitionValidationCode code,
        string message,
        string? target) =>
        Reject(new DefinitionValidationIssue(code, message, target));

    private static LayoutGridBounds? CreateBounds(
        long column,
        long row,
        long columnSpan,
        long rowSpan)
    {
        var valuesFit = column is >= int.MinValue and <= int.MaxValue
            && row is >= int.MinValue and <= int.MaxValue
            && columnSpan is >= int.MinValue and <= int.MaxValue
            && rowSpan is >= int.MinValue and <= int.MaxValue;
        return valuesFit
            ? new((int)column, (int)row, (int)columnSpan, (int)rowSpan)
            : null;
    }

    private static string FormatIssues(IEnumerable<DefinitionValidationIssue> issues) =>
        string.Join(" ", issues.Select(issue => issue.Message));
}
