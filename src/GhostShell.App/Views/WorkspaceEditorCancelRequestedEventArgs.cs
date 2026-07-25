using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed class WorkspaceEditorCancelRequestedEventArgs(
    WorkspaceEditorCancelDisposition disposition) : EventArgs
{
    public WorkspaceEditorCancelDisposition Disposition { get; } = disposition;
}
