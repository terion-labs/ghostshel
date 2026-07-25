using GhostShell.Core;

namespace GhostShell.Application;

public sealed record RegisterWorkspaceGraphRequest
{
    public RegisterWorkspaceGraphRequest(
        WindowInstanceId windowId,
        WorkspaceInstance workspace)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));
        ArgumentNullException.ThrowIfNull(workspace);
        WindowId = windowId;
        Workspace = new WorkspaceInstance(workspace);
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstance Workspace { get; }
}

public sealed record UnregisterWorkspaceGraphRequest
{
    public UnregisterWorkspaceGraphRequest(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId)
    {
        WorkspaceGraphContractValidation.RequireId(windowId.Value, nameof(windowId));
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WindowId = windowId;
        WorkspaceId = workspaceId;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }
}

public sealed record ActivateWorkspaceTabRequest
{
    public ActivateWorkspaceTabRequest(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WorkspaceGraphContractValidation.RequireId(tabId.Value, nameof(tabId));
        WorkspaceId = workspaceId;
        TabId = tabId;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }
}

public sealed record ActivateWorkspacePanelRequest
{
    public ActivateWorkspacePanelRequest(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));
        WorkspaceGraphContractValidation.RequireId(tabId.Value, nameof(tabId));
        WorkspaceGraphContractValidation.RequireId(panelId.Value, nameof(panelId));
        WorkspaceId = workspaceId;
        TabId = tabId;
        PanelId = panelId;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }

    public PanelInstanceId PanelId { get; }
}

public sealed record WatchWorkspaceGraphRequest
{
    public WatchWorkspaceGraphRequest(
        WorkspaceInstanceId workspaceId,
        long afterSequence)
    {
        WorkspaceGraphContractValidation.RequireId(workspaceId.Value, nameof(workspaceId));

        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        WorkspaceId = workspaceId;
        AfterSequence = afterSequence;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public long AfterSequence { get; }
}
