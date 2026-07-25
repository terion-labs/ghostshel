using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ActiveSessionSummary(
    SessionId SessionId,
    PanelInstanceId PanelId,
    string Title,
    string Detail,
    long Revision);
