using GhostShell.Application;

namespace GhostShell.App.ViewModels;

/// <summary>
/// A panel that can ask to be noticed.
///
/// Separate from <see cref="RuntimePanelViewModel"/> because most panel kinds
/// cannot: a file browser has nothing to interrupt anyone about. The shell's
/// notification centre subscribes to the panels that implement this and
/// ignores the rest.
/// </summary>
public interface IPanelNotificationSource
{
    event EventHandler<PanelNotificationEvent>? NotificationReceived;
}

/// <summary>
/// Decides what a panel asking to be noticed does to the shell.
///
/// The rule it enforces is one sentence: a request to be noticed leaves a mark
/// unless you were already looking at the panel that made it, and the mark goes
/// when you look. Everything else here is bookkeeping to make that true at
/// three levels at once — the panel, the tab holding it, and the workspace
/// holding that — because the mark on a workspace tile is only meaningful if it
/// answers "is there anything in here I haven't seen".
///
/// It owns the flags rather than each level computing its own, matching how
/// <see cref="RuntimePanelViewModel.IsActive"/> already works: the levels of
/// the runtime tree know nothing about each other, and giving each one a
/// subscription to its children to answer a question the shell already knows
/// the answer to would be a web of listeners for no gain.
/// </summary>
internal sealed class ShellNotificationCenter
{
    private readonly Dictionary<RuntimePanelViewModel, PanelAttachment> _watched = [];
    private readonly Func<RuntimeWorkspaceViewModel?> _frontWorkspace;
    private readonly Func<bool> _isWindowFocused;
    private readonly Action _flagsChanged;

    /// <param name="frontWorkspace">The workspace on screen, or null at the launcher.</param>
    /// <param name="isWindowFocused">
    /// Whether the shell has the user's attention at all. A notification that
    /// arrives while the window is in the background always leaves a mark, even
    /// for the panel that would otherwise have been considered "being looked
    /// at" — nobody was looking.
    /// </param>
    /// <param name="flagsChanged">
    /// Called after any flag moves, so the workspace rails can be re-derived.
    /// </param>
    public ShellNotificationCenter(
        Func<RuntimeWorkspaceViewModel?> frontWorkspace,
        Func<bool> isWindowFocused,
        Action flagsChanged)
    {
        _frontWorkspace = frontWorkspace ?? throw new ArgumentNullException(nameof(frontWorkspace));
        _isWindowFocused = isWindowFocused ?? throw new ArgumentNullException(nameof(isWindowFocused));
        _flagsChanged = flagsChanged ?? throw new ArgumentNullException(nameof(flagsChanged));
    }

    /// <summary>
    /// Starts listening to every panel in a workspace. Safe to call again for a
    /// workspace already being listened to — reopening one that was never
    /// closed must not double up.
    /// </summary>
    public void Watch(RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                Watch(workspace, tab, panel);
            }
        }
    }

    public void Watch(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(panel);
        if (_watched.ContainsKey(panel) || panel is not IPanelNotificationSource source)
        {
            return;
        }

        void Handler(object? sender, PanelNotificationEvent notification)
        {
            _ = sender;
            _ = notification;
            OnNotification(workspace, tab, panel);
        }

        _watched[panel] = new PanelAttachment(workspace, tab, source, Handler);
        source.NotificationReceived += Handler;
    }

    /// <summary>Stops listening to a workspace and drops its marks.</summary>
    public void Forget(RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (var (panel, attachment) in _watched
            .Where(entry => ReferenceEquals(entry.Value.Workspace, workspace))
            .ToArray())
        {
            attachment.Source.NotificationReceived -= attachment.Handler;
            _watched.Remove(panel);
        }

        workspace.HasAttention = false;
        _flagsChanged();
    }

    public void ForgetAll()
    {
        foreach (var attachment in _watched.Values)
        {
            attachment.Source.NotificationReceived -= attachment.Handler;
        }

        _watched.Clear();
    }

    /// <summary>
    /// Clears whatever the user can currently see. Called wherever the front
    /// workspace, its active tab, its active panel, or the window's focus
    /// changes — those are the four ways "being looked at" can become true.
    /// </summary>
    public void MarkVisibleSeen()
    {
        if (!_isWindowFocused()
            || _frontWorkspace() is not { ActiveTab: { ActivePanel: { } panel } tab } workspace)
        {
            return;
        }

        if (!panel.HasAttention)
        {
            return;
        }

        panel.HasAttention = false;
        Reaggregate(workspace, tab);
    }

    private void OnNotification(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel)
    {
        if (IsBeingLookedAt(workspace, tab, panel))
        {
            return;
        }

        panel.HasAttention = true;
        Reaggregate(workspace, tab);
    }

    private bool IsBeingLookedAt(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel) =>
        _isWindowFocused()
        && ReferenceEquals(_frontWorkspace(), workspace)
        && ReferenceEquals(workspace.ActiveTab, tab)
        && ReferenceEquals(tab.ActivePanel, panel);

    /// <summary>
    /// A tab is marked when any of its panels is; a workspace when any of its
    /// tabs is. Recomputed from the panels rather than counted up and down,
    /// because a count that drifts is a dot that never goes away.
    /// </summary>
    private void Reaggregate(RuntimeWorkspaceViewModel workspace, RuntimeTabViewModel tab)
    {
        tab.HasAttention = tab.Panels.Any(candidate => candidate.HasAttention);
        workspace.HasAttention = workspace.Tabs.Any(candidate => candidate.HasAttention);
        _flagsChanged();
    }

    private sealed record PanelAttachment(
        RuntimeWorkspaceViewModel Workspace,
        RuntimeTabViewModel Tab,
        IPanelNotificationSource Source,
        EventHandler<PanelNotificationEvent> Handler);
}
