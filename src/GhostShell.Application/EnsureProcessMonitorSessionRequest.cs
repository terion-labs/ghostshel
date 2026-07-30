using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureProcessMonitorSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    ConnectionProfile Connection)
{
    public EnsureProcessMonitorSessionRequest(
        SessionId sessionId,
        SessionOwner owner,
        string title)
        : this(sessionId, owner, title, BuiltInConnections.Local)
    {
    }
}
