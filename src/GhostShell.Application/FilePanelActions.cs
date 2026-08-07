namespace GhostShell.Application;

/// <summary>
/// Something a file panel can be asked to do to what is in front of it.
///
/// The list is deliberately provider-neutral. Whether a given connection can
/// actually do any of it is decided in one place — <see cref="FilePanelActionCatalog"/>
/// — from the capabilities that connection declares, so a toolbar, an overflow
/// menu and a right-click menu offering the same action never disagree about
/// whether it is possible.
/// </summary>
public enum FilePanelAction
{
    /// <summary>Hand the file to the operating system's own application for it.</summary>
    OpenExternally,

    /// <summary>Copy what is selected into a folder on this machine.</summary>
    Download,

    /// <summary>Send a file from this machine into the folder being shown.</summary>
    Upload,

    /// <summary>Open the transfer editor for what is selected.</summary>
    Transfer,

    NewFolder,

    Rename,

    Delete,
}

/// <summary>
/// Which band of a menu an action belongs to. Menus draw a rule where the group
/// changes, so the grouping is stated once here rather than by hand at each of
/// the places the actions are shown.
/// </summary>
public enum FilePanelActionGroup
{
    /// <summary>Reaching what is selected.</summary>
    Open,

    /// <summary>Moving bytes between this machine and the connection.</summary>
    Transfer,

    /// <summary>Changing what the folder contains.</summary>
    Organise,
}

/// <summary>
/// Everything the rules need to know, gathered by the panel and answered by the
/// catalog. It is a value rather than the panel itself so the rules can be
/// stated — and tested — without a live provider behind them.
/// </summary>
public sealed record FilePanelActionContext
{
    /// <summary>What the connection says it can do.</summary>
    public FilePanelCapability Capabilities { get; init; }

    /// <summary>A request is in flight, so nothing new should be started.</summary>
    public bool IsBusy { get; init; }

    /// <summary>The panel is still waiting for a saved session to bind.</summary>
    public bool IsBindingSavedSession { get; init; }

    /// <summary>The panel is showing a folder rather than nothing.</summary>
    public bool HasLocation { get; init; }

    /// <summary>
    /// The location is a path with a parent, rather than a flat namespace of
    /// keys. Object stores have no folder to create one inside.
    /// </summary>
    public bool IsHierarchicalLocation { get; init; }

    public int SelectionCount { get; init; }

    /// <summary>Exactly one file — not a folder, not several things.</summary>
    public bool SelectionIsSingleFile { get; init; }

    /// <summary>Everything selected is a file or a folder, so it can be moved.</summary>
    public bool SelectionIsTransferable { get; init; }

    /// <summary>A transfer queue is attached to carry bytes.</summary>
    public bool HasTransferQueue { get; init; }

    /// <summary>This session can reach the machine the shell is running on.</summary>
    public bool HasLocalProvider { get; init; }

    /// <summary>The connection in front of the panel is that machine.</summary>
    public bool IsLocalProvider { get; init; }
}

/// <summary>
/// One action as it stands right now: whether this connection can do it at all,
/// and whether it can be done to what is selected at this moment.
///
/// The two are separate on purpose. A connection that cannot rename should not
/// show a greyed-out Rename forever — that reads as a fault. A connection that
/// can rename but has nothing selected should, because the way to enable it is
/// obvious.
/// </summary>
public sealed record FilePanelActionState(
    FilePanelAction Action,
    FilePanelActionGroup Group,
    bool IsAvailable,
    bool IsEnabled);

/// <summary>
/// The one place that decides what a file connection can be asked to do.
/// </summary>
public static class FilePanelActionCatalog
{
    /// <summary>
    /// Every action in the order menus show them, each answered for this panel.
    /// Callers filter: a menu drops the unavailable, a toolbar shows a few of
    /// them, and both grey out what is available but not currently possible.
    /// </summary>
    public static IReadOnlyList<FilePanelActionState> Resolve(FilePanelActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ready = context is { IsBusy: false, IsBindingSavedSession: false };
        return
        [
            State(
                FilePanelAction.OpenExternally,
                FilePanelActionGroup.Open,
                available: context.IsLocalProvider,
                possible: context.SelectionIsSingleFile),
            State(
                FilePanelAction.Download,
                FilePanelActionGroup.Transfer,
                available: context.HasTransferQueue && context.HasLocalProvider,
                possible: context.SelectionIsTransferable && context.SelectionCount > 0),
            State(
                FilePanelAction.Upload,
                FilePanelActionGroup.Transfer,
                // Uploading into the machine the shell is running on is what
                // the file picker is for; this action is for the other end.
                available: context.HasTransferQueue
                    && context.HasLocalProvider
                    && !context.IsLocalProvider
                    && context.Capabilities.HasFlag(FilePanelCapability.StreamingWrite),
                possible: context.HasLocation),
            State(
                FilePanelAction.Transfer,
                FilePanelActionGroup.Transfer,
                available: context.HasTransferQueue,
                possible: context.SelectionCount > 0 && context.SelectionIsTransferable),
            State(
                FilePanelAction.NewFolder,
                FilePanelActionGroup.Organise,
                available: context.Capabilities.HasFlag(FilePanelCapability.CreateDirectory),
                possible: context.IsHierarchicalLocation),
            State(
                FilePanelAction.Rename,
                FilePanelActionGroup.Organise,
                available: context.Capabilities.HasFlag(FilePanelCapability.Rename),
                possible: context.SelectionCount == 1),
            State(
                FilePanelAction.Delete,
                FilePanelActionGroup.Organise,
                available: context.Capabilities.HasFlag(FilePanelCapability.Delete),
                possible: context.SelectionCount >= 1),
        ];

        FilePanelActionState State(
            FilePanelAction action,
            FilePanelActionGroup group,
            bool available,
            bool possible) => new(action, group, available, available && possible && ready);
    }

    /// <summary>
    /// Whether one action is possible right now, for the guards that run when a
    /// menu item is clicked. Asking the catalog rather than repeating its rules
    /// is what keeps the guard and the greying-out from drifting apart.
    /// </summary>
    public static bool IsEnabled(FilePanelActionContext context, FilePanelAction action) =>
        Resolve(context).Single(state => state.Action == action).IsEnabled;
}
