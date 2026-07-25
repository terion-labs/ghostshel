using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ProcessMonitorHostRequest(
    SessionId SessionId,
    ProcessMonitorQuery Query);
