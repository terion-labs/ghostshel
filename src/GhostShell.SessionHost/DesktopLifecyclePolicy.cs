using GhostShell.Application;

namespace GhostShell.SessionHost;

public sealed class DesktopLifecyclePolicy : ISessionLifecyclePolicy
{
    public HostMode HostMode => GhostShell.Application.HostMode.Desktop;

    public bool ClientDisconnectClosesSessions => false;
}
