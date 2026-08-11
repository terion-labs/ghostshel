using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Views;

/// <summary>
/// The confirmations the shell asks, each written once. A call site states
/// which question it is asking and the facts that fill it in; the wording,
/// the tone, and the button labels live here so the same question is never
/// phrased two ways.
/// </summary>
internal static class Confirmations
{
    public static ConfirmationDialog FileDelete(
        string kind,
        string name,
        string provider,
        bool hasVersionHistory) =>
        new(new ConfirmationDialogOptions
        {
            Title = "Delete file item",
            Heading = $"Delete {kind}?",
            Detail = $"“{name}” will be removed from {provider}.",
            Notice = hasVersionHistory
                ? "GhostShell cannot undo this operation. The provider advertises version history, so an administrator may be able to recover an older version."
                : "This provider does not advertise trash or version recovery. GhostShell cannot undo this permanent deletion.",
            ConfirmLabel = "Delete permanently",
        });

    public static ConfirmationDialog DefinitionDelete(string definitionKind, string name) =>
        DefinitionDelete(
            $"Delete {definitionKind}?",
            $"“{name}” will be permanently removed from this profile.",
            "Referenced definitions are protected and will not be deleted.",
            "Delete");

    public static ConfirmationDialog DefinitionDelete(
        string heading,
        string detail,
        string notice,
        string confirmLabel) =>
        new(new ConfirmationDialogOptions
        {
            Title = "Confirm delete",
            Heading = heading,
            Detail = detail,
            Notice = notice,
            ConfirmLabel = confirmLabel,
            ConfirmAutomationName = "Confirm delete",
            CancelAutomationName = "Cancel delete",
        });

    public static ConfirmationDialog DiscardChanges(
        string heading = "Discard layout changes?",
        string detail = "The unsaved grid, panel geometry, order, and minimum-size changes will be lost.") =>
        new(new ConfirmationDialogOptions
        {
            Title = "Discard changes?",
            Heading = heading,
            Detail = detail,
            ConfirmLabel = "Discard changes",
            CancelLabel = "Keep editing",
            ConfirmAutomationName = "Discard unsaved changes",
            CancelAutomationName = "Keep editing",
        });

    public static ConfirmationDialog HistoryClear() =>
        new(new ConfirmationDialogOptions
        {
            Title = "Clear recent sessions",
            Heading = "Clear recent sessions?",
            Detail = "Recent definition metadata will be removed from this profile.",
            Notice = "This does not close active sessions or delete saved connections, screens, or workspaces. Terminal content and commands were never stored here.",
            ConfirmLabel = "Clear",
            ConfirmAutomationName = "Confirm clear history",
            CancelAutomationName = "Cancel clear history",
        });

    public static ConfirmationDialog HistoryReset() =>
        new(new ConfirmationDialogOptions
        {
            Title = "Reset session history",
            Heading = "Reset unreadable history?",
            Detail = "GhostSHELL could not safely read the retained session metadata. Resetting removes every history row, including malformed rows, so the local store can start clean.",
            Notice = "This recovery action does not close active sessions or delete saved connections, screens, or workspaces. It cannot be limited to the rows shown because unreadable rows are hidden.",
            ConfirmLabel = "Reset history",
            ConfirmAutomationName = "Confirm reset unreadable history",
            CancelAutomationName = "Cancel history reset",
        });

    public static ConfirmationDialog HistoryRetentionChange(HistoryRetentionOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return new ConfirmationDialog(new ConfirmationDialogOptions
        {
            Title = "Change session-history retention",
            Heading = "Apply stricter history retention?",
            Detail = $"{option.DisplayName}: {option.Description}",
            Notice = "Records outside the new limit are removed immediately and cannot be restored. Active sessions stay open, and saved connections, screens, and workspaces are not changed.",
            ConfirmLabel = "Apply and prune",
            ConfirmAutomationName = "Confirm history retention change",
            CancelAutomationName = "Cancel history retention change",
        });
    }

    public static ConfirmationDialog LocalArtifactClear(LocalArtifactItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return LocalArtifactClear(
            $"Clear {item.Title.ToLowerInvariant()}?",
            $"Current inventory: {item.FileCountLabel}, {item.SizeLabel}. "
            + "All eligible files present in this category when cleanup starts "
            + "will be permanently removed from GhostSHELL’s dedicated storage location.",
            $"Confirm {item.ClearAutomationName.ToLowerInvariant()}");
    }

    public static ConfirmationDialog LocalArtifactClear(
        string heading = "Clear selected app-managed files?",
        string detail = "The selected files will be permanently removed.",
        string confirmAutomationName = "Confirm clearing selected app-managed files") =>
        new(new ConfirmationDialogOptions
        {
            Title = "Clear app-managed storage",
            Heading = heading,
            Detail = detail,
            Notice = "Saved definitions, secrets, history, recovery snapshots, terminal content, OS-managed data, and active sessions are unaffected.",
            ConfirmLabel = "Clear files",
            ConfirmAutomationName = confirmAutomationName,
            CancelAutomationName = "Cancel clearing app-managed storage",
        });

    public static ConfirmationDialog RecoveryDataClear(
        string heading = "Clear saved crash recovery data?",
        string detail = "Recovery snapshots from previous runs will be permanently removed from this profile.",
        string confirmLabel = "Clear recovery data") =>
        new(new ConfirmationDialogOptions
        {
            Title = "Clear crash recovery data",
            Heading = heading,
            Detail = detail,
            Notice = "Saved connections, screens, workspaces, secrets, session history, active sessions, and the current run’s protected crash snapshot are unaffected.",
            ConfirmLabel = confirmLabel,
            ConfirmAutomationName = "Confirm clearing crash recovery data",
            CancelAutomationName = "Cancel clearing crash recovery data",
        });

    public static ConfirmationDialog CloseScope(
        CloseScopeResult.ConfirmationRequired confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return new ConfirmationDialog(new ConfirmationDialogOptions
        {
            Title = "Confirm close",
            Heading = confirmation.Scope switch
            {
                CloseScopeKind.Panel => "Close this panel?",
                CloseScopeKind.Tab => "Close this tab?",
                // Terminate, not close: a tab or a window is put away and its
                // sessions happen to end, while this is asked for in order to
                // end them. The word should say which one you are doing.
                CloseScopeKind.Workspace => "Terminate this workspace?",
                CloseScopeKind.Window => "Close GhostSHELL?",
                CloseScopeKind.Session => "Close this session?",
                _ => "Close active sessions?",
            },
            // Not "has active work" — that claims more than the signal
            // supports; idleness is inferred from prompt shape and can only be
            // confirmed, never refuted.
            Detail = confirmation.Sessions.Count == 1
                ? "This session could not be confirmed idle. Closing it ends the session."
                : $"{confirmation.Sessions.Count} sessions could not be confirmed idle. Closing ends them.",
            Notice = string.Join(
                Environment.NewLine,
                confirmation.Sessions.Select(item => $"• {item.Title} — {item.Detail}")),
            NoticeTone = SurfaceTone.Warning,
            ConfirmLabel = "Close anyway",
            ConfirmAutomationName = "Confirm force close",
            CancelAutomationName = "Cancel close",
        });
    }

    public static ConfirmationDialog OperationError(string message) =>
        new(new ConfirmationDialogOptions
        {
            Title = "GhostSHELL error",
            Heading = "Unable to close",
            Detail = message,
            ConfirmLabel = "Close",
            ShowsCancel = false,
            Intent = ConfirmationIntent.Acknowledge,
            ConfirmAutomationName = "Close error dialog",
        });
}
