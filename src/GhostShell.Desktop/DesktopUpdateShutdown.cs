using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace GhostShell.Desktop;

internal sealed class DesktopUpdateShutdown
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;

    public void Attach(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _lifetime = lifetime;
    }

    public void Detach() => _lifetime = null;

    public void Request()
    {
        var lifetime = _lifetime
            ?? throw new InvalidOperationException(
                "The desktop lifetime is not ready for an update restart.");

        Dispatcher.UIThread.Post(() => lifetime.Shutdown());
    }
}
