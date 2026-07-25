using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public enum LayoutDesignerDirection
{
    Left,
    Right,
    Up,
    Down,
}

public enum LayoutDesignerEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

public enum LayoutDesignerCancelDisposition
{
    Close,
    ConfirmDiscard,
}

public sealed record LayoutDesignerOperationResult
{
    private LayoutDesignerOperationResult(
        bool isSuccess,
        DefinitionValidationIssue? issue)
    {
        IsSuccess = isSuccess;
        Issue = issue;
    }

    public static LayoutDesignerOperationResult Applied { get; } = new(true, null);

    public bool IsSuccess { get; }

    public DefinitionValidationIssue? Issue { get; }

    internal static LayoutDesignerOperationResult Rejected(DefinitionValidationIssue issue) =>
        new(false, issue ?? throw new ArgumentNullException(nameof(issue)));
}

public sealed record LayoutDesignerSaveRequest(
    LayoutDefinition Definition,
    long? ExpectedRevision);

/// <summary>
/// Immutable presentation snapshot for one layout slot. <see cref="Order"/> is also the
/// keyboard and accessibility traversal position.
/// </summary>
public sealed record LayoutDesignerSlotViewModel(
    int Order,
    LayoutSlotDefinition Definition,
    bool IsSelected)
{
    public LayoutSlotId Id => Definition.Id;

    public LayoutGridBounds Bounds => Definition.Bounds;

    public LayoutMinimumSize MinimumSize => Definition.MinimumSize;

    public string OrderLabel => $"Panel {Order}";

    public bool UsesOrangePalette => PaletteIndex == 0;

    public bool UsesBluePalette => PaletteIndex == 1;

    public bool UsesGreenPalette => PaletteIndex == 2;

    public bool UsesPinkPalette => PaletteIndex == 3;

    public string PositionLabel =>
        $"Column {Bounds.Column + 1}, row {Bounds.Row + 1}, "
        + $"{Bounds.ColumnSpan} by {Bounds.RowSpan}";

    private int PaletteIndex => (Order - 1) % 4;
}
