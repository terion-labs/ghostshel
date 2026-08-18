using GhostShell.Application;

namespace GhostShell.App.ViewModels;

/// <summary>
/// A panel that can ask to be noticed. The shell subscribes only to panels
/// implementing this capability; ordinary state changes do not become alerts.
/// </summary>
public interface IPanelNotificationSource
{
    event EventHandler<PanelNotificationEvent>? NotificationReceived;
}
