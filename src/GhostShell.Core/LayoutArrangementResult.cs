namespace GhostShell.Core;

public sealed record LayoutArrangementResult(
    IReadOnlyList<LayoutSlotPlacement> Placements,
    DefinitionValidationResult Validation)
{
    public bool IsSuccess => Validation.IsValid;
}
