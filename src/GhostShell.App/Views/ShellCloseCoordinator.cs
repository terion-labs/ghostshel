using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

/// <summary>
/// Owns shell close preflight, confirmation round trips, and the state that
/// distinguishes a user close request from the approved native close.
/// </summary>
internal sealed class ShellCloseCoordinator(
    MainWindowViewModel viewModel,
    ShellClosePresentation presentation,
    CancellationToken lifetime)
{
    private bool _windowCloseApproved;
    private bool _windowCloseInProgress;

    public bool IsWindowCloseApproved => _windowCloseApproved;

    public bool IsWindowCloseInProgress => _windowCloseInProgress;

    public Task RequestWindowCloseAsync()
    {
        if (_windowCloseApproved || _windowCloseInProgress)
        {
            return Task.CompletedTask;
        }

        _windowCloseInProgress = true;
        return CloseWindowCoreAsync();
    }

    public async Task<bool> TryCloseOverlayAsync()
    {
        if (viewModel.IsLayoutDesignerVisible
            && viewModel.LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard
            && !await presentation.ConfirmLayoutDiscardAsync())
        {
            return false;
        }

        if (viewModel.IsDefinitionEditorVisible
            && viewModel.WorkspaceEditor?.RequestCancel()
                == WorkspaceEditorCancelDisposition.ConfirmDiscard
            && !await presentation.ConfirmDiscardAsync(
                "Discard workspace changes?",
                "The unsaved workspace order, tabs, panels, and startup settings will be lost."))
        {
            return false;
        }

        if (viewModel.IsLayoutDesignerVisible)
        {
            viewModel.DismissLayoutDesigner();
        }
        else if (viewModel.IsDefinitionEditorVisible)
        {
            viewModel.DismissWorkspaceEditor();
        }
        else
        {
            viewModel.CloseOverlay();
        }

        presentation.FocusCurrentRoute();
        return true;
    }

    public Task<bool> RunHostCloseAsync(
        Func<CloseDecision, CancellationToken, ValueTask<HostResult<CloseScopeResult>>> close) =>
        MainWindowCloseFlow.RunAsync(
            close,
            presentation.ConfirmScopeAsync,
            presentation.ShowErrorAsync,
            presentation.RestoreFocus,
            lifetime);

    public async Task<bool> ConfirmDiscardDatabaseChangesAsync(
        IEnumerable<RuntimePanelViewModel> panels)
    {
        var dirtyPanels = panels
            .OfType<DatabaseRuntimePanelViewModel>()
            .Where(panel => panel.HasPendingChanges)
            .ToArray();
        if (dirtyPanels.Length == 0)
        {
            return true;
        }

        var detail = dirtyPanels.Length == 1
            ? $"The unsaved row changes in {dirtyPanels[0].SelectedObjectName} will be lost."
            : $"Unsaved row changes in {dirtyPanels.Length} database panels will be lost.";
        return await presentation.ConfirmDiscardAsync(
            "Discard database changes?",
            detail);
    }

    private async Task CloseWindowCoreAsync()
    {
        try
        {
            if (viewModel.LayoutDesignerEditor?.RequestCancel()
                    == LayoutDesignerCancelDisposition.ConfirmDiscard
                && !await presentation.ConfirmLayoutDiscardAsync())
            {
                return;
            }

            if (viewModel.KeybindingEditorSession?.IsDirty == true
                && !await presentation.ConfirmDiscardAsync(
                    "Discard keybinding changes?",
                    "The unsaved shortcuts, prefix, and conflict resolutions will be lost when GhostShell closes."))
            {
                return;
            }

            if (viewModel.WorkspaceEditor?.RequestCancel()
                    == WorkspaceEditorCancelDisposition.ConfirmDiscard
                && !await presentation.ConfirmDiscardAsync(
                    "Discard workspace changes?",
                    "The unsaved workspace order, tabs, panels, and startup settings will be lost when GhostShell closes."))
            {
                return;
            }

            if (!await ConfirmDiscardDatabaseChangesAsync(
                viewModel.OpenWorkspaces.SelectMany(workspace =>
                    workspace.Tabs.SelectMany(tab => tab.Panels))))
            {
                return;
            }

            if (await RunHostCloseAsync(viewModel.CloseWindowAsync))
            {
                await viewModel.QuiesceForShutdownAsync(CancellationToken.None);
                _windowCloseApproved = true;
                presentation.CloseWindow();
            }
        }
        finally
        {
            if (!_windowCloseApproved)
            {
                viewModel.ResumeAfterWindowCloseAttempt();
            }

            _windowCloseInProgress = false;
        }
    }
}
