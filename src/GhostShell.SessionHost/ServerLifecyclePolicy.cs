using GhostShell.Application;

namespace GhostShell.SessionHost;

public sealed class ServerLifecyclePolicy : ISessionLifecyclePolicy
{
    public HostMode HostMode => GhostShell.Application.HostMode.Server;

    public bool ClientDisconnectClosesSessions => false;
}
