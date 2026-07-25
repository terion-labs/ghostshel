using GhostShell.Application;

namespace GhostShell.SessionHost;

public interface ISessionLifecyclePolicy
{
    HostMode HostMode { get; }

    bool ClientDisconnectClosesSessions { get; }
}
