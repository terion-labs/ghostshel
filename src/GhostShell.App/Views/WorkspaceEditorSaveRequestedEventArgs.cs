using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed class WorkspaceEditorSaveRequestedEventArgs(
    WorkspaceEditorSaveRequest request) : EventArgs
{
    public WorkspaceEditorSaveRequest Request { get; } = request
        ?? throw new ArgumentNullException(nameof(request));
}
