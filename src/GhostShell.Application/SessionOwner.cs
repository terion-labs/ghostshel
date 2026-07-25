using GhostShell.Core;

namespace GhostShell.Application;

public sealed record SessionOwner(
    HostMode HostMode,
    WindowInstanceId WindowId,
    WorkspaceInstanceId WorkspaceId,
    TabInstanceId TabId,
    PanelInstanceId PanelId);
