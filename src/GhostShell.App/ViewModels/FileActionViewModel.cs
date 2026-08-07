using System.Windows.Input;

using FluentIcons.Common;

using GhostShell.Application;

namespace GhostShell.App.ViewModels;

/// <summary>
/// One file action, dressed for showing.
///
/// The same instances are handed to the toolbar, to the overflow menu and to
/// the right-click menu. That is the point: an action that a connection cannot
/// perform disappears from all three at once, and one that is momentarily
/// impossible greys out in all three, without any of them knowing the rules.
/// </summary>
public sealed class FileActionViewModel
{
    public FileActionViewModel(
        FilePanelActionState state,
        string label,
        string description,
        Symbol symbol,
        bool startsGroup,
        Action<FilePanelAction> request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(request);
        Command = new FileActionCommand(state, request);
        Action = state.Action;
        Group = state.Group;
        IsEnabled = state.IsEnabled;
        Label = label;
        Description = description;
        Symbol = symbol;
        StartsGroup = startsGroup;
    }

    public FilePanelAction Action { get; }

    public FilePanelActionGroup Group { get; }

    public bool IsEnabled { get; }

    /// <summary>What the action is called, in a menu.</summary>
    public string Label { get; }

    /// <summary>
    /// What it will do, for the tooltip and for the screen reader — which is
    /// all a toolbar button has, since it shows only a symbol.
    /// </summary>
    public string Description { get; }

    public Symbol Symbol { get; }

    /// <summary>
    /// This action opens a new band of the menu, so a rule is drawn above it.
    /// Computed once when the list is built, because whether a rule belongs
    /// there depends on which actions before it survived.
    /// </summary>
    public bool StartsGroup { get; }

    /// <summary>Deleting is drawn in the danger colour wherever it appears.</summary>
    public bool IsDestructive => Action == FilePanelAction.Delete;

    /// <summary>
    /// What a button or a menu row invokes. A command rather than a click
    /// handler because a menu opens in a window of its own, where a routed
    /// event never reaches the panel that owns the menu.
    /// </summary>
    public ICommand Command { get; }

    private sealed class FileActionCommand(
        FilePanelActionState state,
        Action<FilePanelAction> request) : ICommand
    {
        // The list is rebuilt whenever anything could change the answer, so
        // this instance's answer never does.
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => state.IsEnabled;

        public void Execute(object? parameter)
        {
            if (state.IsEnabled)
            {
                request(state.Action);
            }
        }
    }
}

/// <summary>
/// What each action is called and what it looks like. Kept apart from the rules
/// that decide whether it is possible: one is a matter of language, the other of
/// what the connection can do.
/// </summary>
internal static class FilePanelActionPresentation
{
    public static string Label(FilePanelAction action) => action switch
    {
        FilePanelAction.OpenExternally => "Open",
        FilePanelAction.Download => "Download…",
        FilePanelAction.Upload => "Upload…",
        FilePanelAction.Transfer => "Transfer…",
        FilePanelAction.NewFolder => "New folder…",
        FilePanelAction.Rename => "Rename…",
        FilePanelAction.Delete => "Delete",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static Symbol Glyph(FilePanelAction action) => action switch
    {
        FilePanelAction.OpenExternally => Symbol.OpenFolder,
        FilePanelAction.Download => Symbol.DocumentArrowDown,
        FilePanelAction.Upload => Symbol.ArrowUpload,
        FilePanelAction.Transfer => Symbol.ArrowSwap,
        FilePanelAction.NewFolder => Symbol.FolderAdd,
        FilePanelAction.Rename => Symbol.Edit,
        FilePanelAction.Delete => Symbol.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static string Description(FilePanelAction action) => action switch
    {
        FilePanelAction.OpenExternally => "Open the selected file in its default application",
        FilePanelAction.Download => "Download the selected items to a folder",
        FilePanelAction.Upload => "Upload a file from this machine",
        FilePanelAction.Transfer => "Transfer the selected item",
        FilePanelAction.NewFolder => "Create a folder here",
        FilePanelAction.Rename => "Rename the selected item",
        FilePanelAction.Delete => "Delete the selected item",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
