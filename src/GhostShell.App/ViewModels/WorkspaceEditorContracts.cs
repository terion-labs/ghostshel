using FluentIcons.Common;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public enum WorkspaceEditorEntryKind
{
    Connection,
    SavedScreen,
    WorkspaceTab,
}

public enum WorkspaceEditorCancelDisposition
{
    Close,
    ConfirmDiscard,
}

public sealed record WorkspaceEditorSaveRequest(
    WorkspaceDefinition Definition,
    long? ExpectedRevision);

public sealed record WorkspaceEditorOperationResult(
    bool IsSuccess,
    WorkspaceEntryId? EntryId,
    string? Error)
{
    public static WorkspaceEditorOperationResult Applied(WorkspaceEntryId entryId) =>
        new(true, entryId, null);

    public static WorkspaceEditorOperationResult Rejected(string error) =>
        new(false, null, error);
}

public sealed record WorkspaceScreenOption(
    ScreenId Id,
    string Name,
    string LayoutName,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable
        ? $"{Name} · {LayoutName}"
        : $"Missing · {Name}";
}

public sealed record WorkspaceLayoutOption(
    LayoutId Id,
    string Name,
    bool IsAvailable,
    LayoutDefinition? Definition)
{
    public string DisplayName => IsAvailable ? Name : $"Missing · {Name}";
}

public sealed record WorkspaceLayoutSlotOption(
    LayoutSlotId Id,
    string Name,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable ? Name : $"Missing · {Name}";
}

/// <summary>
/// One choice in the workspace icon picker. <paramref name="Keywords"/> exists so
/// the picker can be searched by purpose ("prod", "db") and not only by the
/// icon's own name.
/// </summary>
public sealed record WorkspaceIconOption(
    string Id,
    string Name,
    Symbol Symbol = Symbol.Window,
    string Keywords = "")
{
    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Keywords.Contains(term, StringComparison.OrdinalIgnoreCase);
}
