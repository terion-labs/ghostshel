using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureStatisticsSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    ConnectionProfile Connection)
{
    public EnsureStatisticsSessionRequest(
        SessionId sessionId,
        SessionOwner owner,
        string title)
        : this(sessionId, owner, title, BuiltInConnections.Local)
    {
    }
}
