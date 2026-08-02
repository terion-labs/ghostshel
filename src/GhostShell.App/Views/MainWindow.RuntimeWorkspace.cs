using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentIcons.Common;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.App.Views.RuntimePanels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    private const double RuntimeTabDragThreshold = 6;
    private static readonly DataFormat<RuntimeTabDragPayload> RuntimeTabDragFormat =
        DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>(
            "app.ghostshell.runtime-tab");

    private FilePanelTransferPayload? _fileTransferClipboard;
    private RuntimeTabActiveDrag? _runtimeTabActiveDrag;
    private RuntimeTabDragCandidate? _runtimeTabDragCandidate;
    private Grid? _runtimeTabDropTarget;
    private bool _runtimeTabDragInProgress;

    public async Task RequestNewTerminalAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (ResolveNewTerminalTarget(ViewModel.HasRuntimeWorkspace)
            == NewTerminalTarget.ExistingRuntimeWorkspace)
        {
            ViewModel.ShowWorkspace();
            if (!ViewModel.IsWorkspaceVisible)
            {
                return;
            }

            if (await ViewModel.AddLocalTerminalTabAsync(_lifetime.Token))
            {
                FocusActivePanel();
            }
            return;
        }

        await OpenDefaultLocalTerminalAsync();
    }

    private async void OnSavedConnectionLaunchRequested(
        object? sender,
        SavedConnectionLaunchViewModel launch)
    {
        _ = sender;
        try
        {
            if (await ViewModel.AddSavedConnectionTabAsync(launch, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task RequestNewFileViewerAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            if (ViewModel.Workspaces.FirstOrDefault() is { } workspace)
            {
                await ReplaceRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
            }
            else if (ViewModel.Connections.FirstOrDefault(item => item.CanOpen) is { } connection)
            {
                await ReplaceRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenConnectionAsync(connection.Id, token));
            }
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace or connection before adding a File Viewer.");
            return;
        }

        if (await ViewModel.AddFilePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestNewBrowserAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            await ReplaceRuntimeWorkspaceAsync(
                ViewModel.OpenLocalBrowserWorkspaceAsync);
            return;
        }

        ViewModel.ShowWorkspace();
        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace or connection before adding a browser.");
            return;
        }

        if (await ViewModel.AddBrowserPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public Task RequestNewStatisticsAsync() =>
        RequestNewMonitorAsync(PanelKind.Statistics);

    public Task RequestNewProcessMonitorAsync() =>
        RequestNewMonitorAsync(PanelKind.ProcessMonitor);

    private async Task RequestNewAdapterTabAsync(PanelKind kind)
    {
        if (kind is not (PanelKind.Browser
            or PanelKind.FileViewer
            or PanelKind.Statistics
            or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        // With no runtime workspace, the existing session-start paths create the
        // first workspace and tab. Once a tab exists, the New Tab host must append
        // another tab; only a placed placeholder may add to the active tab.
        if (!ViewModel.HasRuntimeWorkspace)
        {
            await RequestNewAdapterSessionAsync(kind);
            return;
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        ViewModel.ShowWorkspace();
        var added = kind switch
        {
            PanelKind.Browser =>
                await ViewModel.AddBrowserTabAsync(_lifetime.Token),
            PanelKind.FileViewer =>
                await ViewModel.AddFileViewerTabAsync(_lifetime.Token),
            PanelKind.Statistics =>
                await ViewModel.AddStatisticsTabAsync(_lifetime.Token),
            PanelKind.ProcessMonitor =>
                await ViewModel.AddProcessMonitorTabAsync(_lifetime.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        if (added)
        {
            FocusActivePanel();
        }
    }

    private Task RequestNewAdapterSessionAsync(PanelKind kind) => kind switch
    {
        PanelKind.Browser => RequestNewBrowserAsync(),
        PanelKind.FileViewer => RequestNewFileViewerAsync(),
        PanelKind.Statistics => RequestNewStatisticsAsync(),
        PanelKind.ProcessMonitor => RequestNewProcessMonitorAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private async Task RequestNewMonitorAsync(PanelKind kind)
    {
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace)
        {
            await ReplaceRuntimeWorkspaceAsync(token =>
                ViewModel.OpenLocalMonitorWorkspaceAsync(kind, token));
            return;
        }

        ViewModel.ShowWorkspace();
        var added = kind == PanelKind.Statistics
            ? await ViewModel.AddStatisticsPanelAsync(_lifetime.Token)
            : await ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token);
        if (added)
        {
            FocusActivePanel();
        }
    }

    public async Task SelectRelativeTabAsync(int offset)
    {
        if (!ViewModel.IsWorkspaceVisible || ViewModel.HasOverlay)
        {
            return;
        }

        if (await ViewModel.SelectRelativeTabAsync(offset, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    public async Task RequestClosePanelAsync()
    {
        if (ViewModel.HasOverlay)
        {
            _ = await TryCloseOverlayAsync();
            return;
        }

        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        await ExecuteCommandAsync(BuiltInCommands.ClosePanel);
    }

    public async Task RequestCloseTabAsync()
    {
        if (ViewModel.HasOverlay)
        {
            _ = await TryCloseOverlayAsync();
            return;
        }

        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        await ExecuteCommandAsync(BuiltInCommands.CloseTab);
    }

    private async Task OpenDefaultLocalTerminalAsync()
    {
        var connection = ViewModel.Connections.FirstOrDefault(item =>
                item.CanOpen && string.Equals(item.Kind, "Local", StringComparison.OrdinalIgnoreCase))
            ?? ViewModel.Connections.FirstOrDefault(item => item.CanOpen);
        if (connection is null)
        {
            ViewModel.SetError("Create an available connection before opening a terminal.");
            ShowNewItemLauncher();
            return;
        }

        await ReplaceRuntimeWorkspaceAsync(token =>
            ViewModel.OpenConnectionAsync(connection.Id, token));
    }

    internal static NewTerminalTarget ResolveNewTerminalTarget(bool hasRuntimeWorkspace) =>
        hasRuntimeWorkspace
            ? NewTerminalTarget.ExistingRuntimeWorkspace
            : NewTerminalTarget.DefaultConnectionWorkspace;

    private async Task ReplaceRuntimeWorkspaceAsync(
        Func<CancellationToken, Task<bool>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (ViewModel.HasRuntimeWorkspace
            && !await RunCloseFlowAsync(ViewModel.CloseWindowAsync))
        {
            return;
        }

        if (await open(_lifetime.Token))
        {
            ViewModel.CloseOverlay();
            FocusActivePanel();
        }
    }

    private async void OnActivateTabClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: RuntimeTabViewModel tab })
        {
            try
            {
                ViewModel.ShowWorkspace();
                if (!ViewModel.IsWorkspaceVisible)
                {
                    return;
                }

                if (await ViewModel.ActivateTabAsync(tab.Id, _lifetime.Token))
                {
                    FocusActivePanel();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private void OnRuntimeTabDragPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (_runtimeTabDragInProgress
            || sender is not Control
            {
                DataContext: RuntimeTabViewModel tab,
            } source
            || ViewModel.RuntimeWorkspace is not { } workspace
            || workspace.Tabs.Count < 2
            || !e.Pointer.IsPrimary)
        {
            return;
        }

        var point = e.GetCurrentPoint(source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        _runtimeTabDragCandidate = new RuntimeTabDragCandidate(
            source,
            point.Position,
            e.Pointer,
            new RuntimeTabDragPayload(
                ViewModel.WindowId,
                workspace.Id,
                tab.Id,
                tab.Title));
        e.Handled = true;
    }

    private void OnRuntimeTabDragPointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (_runtimeTabActiveDrag is { } active
            && ReferenceEquals(sender, active.Source)
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            var current = e.GetCurrentPoint(active.Source);
            if (!current.Properties.IsLeftButtonPressed
                && e.Pointer.Type != PointerType.Touch)
            {
                CancelRuntimeTabDrag(active.Pointer);
                return;
            }

            UpdateRuntimeTabDrag(e, active);
            e.Handled = true;
            return;
        }

        if (_runtimeTabDragCandidate is not { } candidate
            || !ReferenceEquals(sender, candidate.Source)
            || !ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(candidate.Source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            _runtimeTabDragCandidate = null;
            return;
        }

        var delta = point.Position - candidate.Origin;
        if (Math.Abs(delta.X) < RuntimeTabDragThreshold
            && Math.Abs(delta.Y) < RuntimeTabDragThreshold)
        {
            return;
        }

        _runtimeTabDragCandidate = null;
        _runtimeTabDragInProgress = true;
        e.Handled = true;
        var activeDrag = new RuntimeTabActiveDrag(
            candidate.Source,
            candidate.Pointer,
            candidate.Payload);
        // Capture changes can synchronously report loss to the previous owner.
        // The new drag must not exist until that transition has completed.
        candidate.Pointer.Capture(candidate.Source);
        _runtimeTabActiveDrag = activeDrag;
        ShowDragGhost(
            new DragGhostPayload(
                Symbol.WindowConsole,
                candidate.Payload.Title,
                "Move tab"),
            e.GetPosition(this));
        UpdateRuntimeTabDrag(e, activeDrag);
    }

    private async void OnRuntimeTabDragPointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (_runtimeTabActiveDrag is { } active
            && ReferenceEquals(sender, active.Source)
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            await CompleteRuntimeTabDragAsync(e, active);
            e.Handled = true;
            return;
        }

        _runtimeTabDragCandidate = null;
    }

    private void OnRuntimeTabDragPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_runtimeTabActiveDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            CancelRuntimeTabDrag(active.Pointer, releaseCapture: false);
            return;
        }

        _runtimeTabDragCandidate = null;
    }

    private void UpdateRuntimeTabDrag(
        PointerEventArgs e,
        RuntimeTabActiveDrag active)
    {
        var position = e.GetPosition(this);
        MoveDragGhost(position);
        if (ResolveRuntimeTabDrop(position, active.Payload) is { } target)
        {
            ShowRuntimeTabDropIndicator(target.Target, target.Placement);
        }
        else
        {
            ClearRuntimeTabDropIndicator();
        }
    }

    private async Task CompleteRuntimeTabDragAsync(
        PointerReleasedEventArgs e,
        RuntimeTabActiveDrag active)
    {
        var target = ResolveRuntimeTabDrop(e.GetPosition(this), active.Payload);
        _runtimeTabActiveDrag = null;
        _runtimeTabDragCandidate = null;
        _runtimeTabDragInProgress = false;
        ClearRuntimeTabDropIndicator();
        active.Pointer.Capture(null);
        HideDragGhost();
        if (target is null)
        {
            return;
        }

        try
        {
            if (await ViewModel.MoveTabAsync(
                    active.Payload.TabId,
                    target.AnchorTabId,
                    target.Placement,
                    _lifetime.Token))
            {
                FocusRuntimeTabButton(active.Payload.TabId);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private void CancelRuntimeTabDrag(
        IPointer pointer,
        bool releaseCapture = true)
    {
        _runtimeTabActiveDrag = null;
        _runtimeTabDragCandidate = null;
        _runtimeTabDragInProgress = false;
        ClearRuntimeTabDropIndicator();
        if (releaseCapture)
        {
            pointer.Capture(null);
        }

        HideDragGhost();
    }

    private RuntimeTabDropTarget? ResolveRuntimeTabDrop(
        Point position,
        RuntimeTabDragPayload payload)
    {
        if (this.InputHitTest(position) is not Visual hit)
        {
            return null;
        }

        var target = hit is Grid grid
            && grid.Classes.Contains("RuntimeTabDropTarget")
                ? grid
                : hit.GetVisualAncestors()
                    .OfType<Grid>()
                    .FirstOrDefault(control =>
                        control.Classes.Contains("RuntimeTabDropTarget"));
        if (target is null
            || !TryResolveRuntimeTabDrop(
                target,
                payload,
                position - target.TranslatePoint(default, this).GetValueOrDefault(),
                out var anchorTabId,
                out var placement))
        {
            return null;
        }

        return new RuntimeTabDropTarget(target, anchorTabId, placement);
    }

    private void OnRuntimeTabDragEnter(object? sender, DragEventArgs e) =>
        UpdateRuntimeTabDropTarget(sender, e);

    private void OnRuntimeTabDragOver(object? sender, DragEventArgs e) =>
        UpdateRuntimeTabDropTarget(sender, e);

    private void OnRuntimeTabDragLeave(object? sender, DragEventArgs e)
    {
        _ = e;
        if (ReferenceEquals(sender, _runtimeTabDropTarget))
        {
            ClearRuntimeTabDropIndicator();
        }
    }

    private async void OnRuntimeTabDrop(object? sender, DragEventArgs e)
    {
        if (!TryResolveRuntimeTabDrop(
                sender,
                e,
                out var payload,
                out var anchorTabId,
                out var placement))
        {
            e.DragEffects = DragDropEffects.None;
            ClearRuntimeTabDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        ClearRuntimeTabDropIndicator();
        try
        {
            if (await ViewModel.MoveTabAsync(
                    payload.TabId,
                    anchorTabId,
                    placement,
                    _lifetime.Token))
            {
                FocusRuntimeTabButton(payload.TabId);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private void UpdateRuntimeTabDropTarget(object? sender, DragEventArgs e)
    {
        if (!TryResolveRuntimeTabDrop(sender, e, out _, out _, out var placement)
            || sender is not Grid target)
        {
            e.DragEffects = DragDropEffects.None;
            ClearRuntimeTabDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        ShowRuntimeTabDropIndicator(target, placement);
    }

    private bool TryResolveRuntimeTabDrop(
        object? sender,
        DragEventArgs e,
        out RuntimeTabDragPayload payload,
        out TabInstanceId anchorTabId,
        out RuntimeTabPlacement placement)
    {
        payload = null!;
        anchorTabId = default;
        placement = default;
        if (sender is not Grid
            {
                DataContext: RuntimeTabViewModel targetTab,
            } target
            || e.DataTransfer.TryGetValue(RuntimeTabDragFormat) is not { } candidate
            || !TryResolveRuntimeTabDrop(
                target,
                candidate,
                e.GetPosition(target),
                out anchorTabId,
                out placement))
        {
            return false;
        }

        payload = candidate;
        return true;
    }

    private bool TryResolveRuntimeTabDrop(
        Grid target,
        RuntimeTabDragPayload candidate,
        Point targetPosition,
        out TabInstanceId anchorTabId,
        out RuntimeTabPlacement placement)
    {
        anchorTabId = default;
        placement = default;
        if (target.DataContext is not RuntimeTabViewModel targetTab
            || ViewModel.RuntimeWorkspace is not { } workspace
            || candidate.WindowId != ViewModel.WindowId
            || candidate.WorkspaceId != workspace.Id
            || candidate.TabId == targetTab.Id
            || workspace.Tabs.All(tab => tab.Id != candidate.TabId))
        {
            return false;
        }

        placement = targetPosition.X < target.Bounds.Width / 2
            ? RuntimeTabPlacement.Before
            : RuntimeTabPlacement.After;
        if (!WouldMoveRuntimeTab(
                workspace,
                candidate.TabId,
                targetTab.Id,
                placement))
        {
            return false;
        }

        anchorTabId = targetTab.Id;
        return true;
    }

    private static bool WouldMoveRuntimeTab(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId sourceTabId,
        TabInstanceId anchorTabId,
        RuntimeTabPlacement placement)
    {
        var source = workspace.Tabs.SingleOrDefault(tab => tab.Id == sourceTabId);
        var anchor = workspace.Tabs.SingleOrDefault(tab => tab.Id == anchorTabId);
        if (source is null || anchor is null)
        {
            return false;
        }

        var sourceIndex = workspace.Tabs.IndexOf(source);
        var anchorIndex = workspace.Tabs.IndexOf(anchor);
        var destinationIndex = placement == RuntimeTabPlacement.Before
            ? anchorIndex
            : anchorIndex + 1;
        if (sourceIndex < destinationIndex)
        {
            destinationIndex--;
        }

        return sourceIndex != destinationIndex;
    }

    private void ShowRuntimeTabDropIndicator(
        Grid target,
        RuntimeTabPlacement placement)
    {
        ClearRuntimeTabDropIndicator();
        _runtimeTabDropTarget = target;
        foreach (var indicator in target.Children
                     .OfType<Border>()
                     .Where(control => control.Classes.Contains("RuntimeTabDropIndicator")))
        {
            indicator.IsVisible = placement == RuntimeTabPlacement.Before
                ? indicator.Classes.Contains("Before")
                : indicator.Classes.Contains("After");
        }
    }

    private void ClearRuntimeTabDropIndicator()
    {
        if (_runtimeTabDropTarget is { } target)
        {
            foreach (var indicator in target.Children
                         .OfType<Border>()
                         .Where(control => control.Classes.Contains("RuntimeTabDropIndicator")))
            {
                indicator.IsVisible = false;
            }
        }

        _runtimeTabDropTarget = null;
    }

    private void FocusRuntimeTabButton(TabInstanceId tabId) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var button = this.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(control =>
                    control.Classes.Contains("RuntimeTabActivator")
                    && control.DataContext is RuntimeTabViewModel tab
                    && tab.Id == tabId);
            button?.BringIntoView();
            button?.Focus(NavigationMethod.Pointer);
        });

    private async void OnRuntimePanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = e;
        await ActivateRuntimePanelAsync(sender);
    }

    private async void OnRuntimePanelGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _ = e;
        await ActivateRuntimePanelAsync(sender);
    }

    private async Task ActivateRuntimePanelAsync(object? sender)
    {
        if (sender is Control { DataContext: RuntimePanelViewModel panel })
        {
            // Selection follows input focus. Do not move focus here: the terminal
            // surface that raised the event must keep keyboard ownership.
            try
            {
                _ = await ViewModel.ActivatePanelAsync(panel.Id, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// Places an empty panel against an edge of the canvas. It asks what to open
    /// once it is there, so the choice happens in the space the panel will fill.
    /// </summary>
    private void OnAddPanelToSideRequested(object? sender, PanelSide side)
    {
        _ = sender;
        if (ViewModel.RuntimeWorkspace?.ActiveTab is { } tab)
        {
            _ = tab.AddPlaceholder(side);
            FocusActivePanel();
        }
    }

    /// <summary>
    /// Splits a panel, leaving an empty one beside it for the user to fill.
    /// </summary>
    private void OnSplitRuntimePanelRequested(object? sender, PanelSplitOrientation orientation)
    {
        if (sender is not Control { DataContext: RuntimePanelViewModel panel }
            || ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        _ = tab.SplitWithPlaceholder(panel.Id, orientation);
        FocusActivePanel();
    }

    /// <summary>
    /// Turns a placeholder into a real panel. The tab is told which placeholder is
    /// being answered so the created panel takes its cell rather than being
    /// appended wherever the layout would have put it.
    ///
    /// These are the panel-level operations deliberately: the toolbar's "new
    /// terminal" action opens a whole tab, which is right when nothing is being
    /// answered and wrong here — it left the placeholder sitting empty and put the
    /// terminal in a new tab instead of the cell the user had just placed.
    /// </summary>
    private async Task ChoosePlaceholderAsync(object? sender, Func<Task<bool>> create)
    {
        if (sender is not Control source
            || ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        var placeholder =
            source.DataContext as PanelPlaceholderViewModel
            ?? source.FindAncestorOfType<RuntimePanels.PanelPlaceholderView>()?.DataContext
                as PanelPlaceholderViewModel;
        if (placeholder is null)
        {
            return;
        }

        tab.ReplaceTarget = placeholder.Id;
        try
        {
            await create();
        }
        finally
        {
            tab.ReplaceTarget = null;
        }
    }

    private async void OnPlaceholderTerminalClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddLocalTerminalPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderBrowserClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddBrowserPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderStatisticsClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddStatisticsPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderFileViewerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddFilePanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderProcessMonitorClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token));
    }

    private async void OnPlaceholderConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherConnectionViewModel connection })
        {
            return;
        }

        await ChoosePlaceholderAsync(
            sender,
            () => ViewModel.AddConnectionPanelAsync(connection.Id, _lifetime.Token));
    }

    private async void OnCloseRuntimePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        if (await CloseRuntimePanelAsync(panel))
        {
            if (await ViewModel.RemovePanelAsync(panel.Id, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
    }

    private async void OnTerminalConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        if (e.Selection is not PanelConnectionOptionViewModel.Target.Connection target)
        {
            return;
        }

        if (sender is not Control
            {
                DataContext: TerminalRuntimePanelViewModel panel,
            }
            || panel.ConnectionId == target.Id)
        {
            return;
        }

        await SwitchTerminalConnectionAsync(
            panel,
            () => ViewModel.ReplaceTerminalConnection(panel, target.Id));
    }

    private async void OnTerminalNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: TerminalRuntimePanelViewModel panel,
            })
        {
            return;
        }

        try
        {
            var editor = ViewModel.CreateConnectionEditor();
            var request = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<ConnectionEditorConnectRequest?>(this);
            if (request is null)
            {
                return;
            }

            if (request.SaveConnection)
            {
                var saved = await ViewModel.SaveConnectionAsync(
                    new ConnectionEditorSaveRequest(request.Profile, null),
                    _lifetime.Token);
                if (!saved.IsSuccess)
                {
                    return;
                }
            }

            await SwitchTerminalConnectionAsync(
                panel,
                () => ViewModel.ReplaceTerminalConnection(panel, request.Profile));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task SwitchTerminalConnectionAsync(
        TerminalRuntimePanelViewModel panel,
        Func<bool> replace)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(replace);
        if (!await CloseRuntimePanelAsync(panel))
        {
            return;
        }

        if (replace())
        {
            FocusActivePanel();
        }
    }

    private async void OnPanelConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimePanelViewModel panel }
            || IsCurrentConnection(panel, e.Selection))
        {
            return;
        }

        if (panel is FileRuntimePanelViewModel files
            && e.Selection is PanelConnectionOptionViewModel.Target.FileProvider fileTarget)
        {
            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplaceFilePanelProfile(files, fileTarget.Id));
            return;
        }

        if (e.Selection is PanelConnectionOptionViewModel.Target.Connection connectionTarget)
        {
            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplacePanelConnection(panel, connectionTarget.Id));
        }
    }

    private async void OnPanelNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimePanelViewModel panel })
        {
            return;
        }

        if (panel is FileRuntimePanelViewModel files)
        {
            await CreateAndSwitchFileConnectionAsync(files);
            return;
        }

        try
        {
            var editor = ViewModel.CreateConnectionEditor();
            var request = await new ConnectionEditorDialog(
                    editor,
                    ConnectionEditorDialogPurpose.Connect)
                .ShowDialog<ConnectionEditorConnectRequest?>(this);
            if (request is null)
            {
                return;
            }

            if (request.SaveConnection)
            {
                var saved = await ViewModel.SaveConnectionAsync(
                    new ConnectionEditorSaveRequest(request.Profile, null),
                    _lifetime.Token);
                if (!saved.IsSuccess)
                {
                    return;
                }
            }

            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplacePanelConnection(panel, request.Profile));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task CreateAndSwitchFileConnectionAsync(
        FileRuntimePanelViewModel panel)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateFileProviderEditor();
            var request = await new FileProviderProfileEditorDialog(
                    editor,
                    FileProviderProfileEditorDialogPurpose.Connect)
                .ShowDialog<FileProviderProfileSaveRequest?>(this);
            if (request is null)
            {
                return;
            }

            var saved = await ViewModel.SaveFileProviderProfileAsync(
                request,
                _lifetime.Token);
            if (!saved.IsSuccess || saved.Value is null)
            {
                return;
            }

            await SwitchRuntimePanelConnectionAsync(
                panel,
                () => ViewModel.ReplaceFilePanelProfile(
                    panel,
                    saved.Value.Value.Id));
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async Task SwitchRuntimePanelConnectionAsync(
        RuntimePanelViewModel panel,
        Func<bool> replace)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(replace);
        if (!await CloseRuntimePanelAsync(panel))
        {
            return;
        }

        if (replace())
        {
            FocusActivePanel();
        }
    }

    private static bool IsCurrentConnection(
        RuntimePanelViewModel panel,
        PanelConnectionOptionViewModel.Target selection) =>
        (panel, selection) switch
        {
            (
                FileRuntimePanelViewModel files,
                PanelConnectionOptionViewModel.Target.FileProvider target) =>
                files.UsesProfile(target.Id),
            (
                StatisticsRuntimePanelViewModel statistics,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                statistics.ConnectionId == target.Id,
            (
                ProcessMonitorRuntimePanelViewModel processes,
                PanelConnectionOptionViewModel.Target.Connection target) =>
                processes.ConnectionId == target.Id,
            _ => false,
        };

    private async void OnRetryConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel }
            && panel.CanRetry)
        {
            await panel.RetryAsync();
        }
    }

    private void OnCancelConnectionReconnectClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.CancelReconnect();
        }
    }

    private async void OnTrustConnectionHostKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: TerminalRuntimePanelViewModel panel }
            || panel.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await panel.TrustHostKeyAsync(_lifetime.Token);
        }
    }

    private void OnTerminalSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e)
    {
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.ObserveSessionSnapshot(e.Snapshot);
        }
    }

    private void OnTerminalSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e)
    {
        if (sender is Control { DataContext: TerminalRuntimePanelViewModel panel })
        {
            panel.ObserveSessionInitializationFailure(e.Failure);
        }
    }

    private void OnBrowserStateChanged(
        object? sender,
        BrowserStateChangedEventArgs e)
    {
        if (sender is BrowserPresentationHost
            {
                DataContext: BrowserRuntimePanelViewModel panel,
            })
        {
            panel.ApplyBrowserState(e.State);
        }
    }

    private async void OnBrowserAddressKeyDown(object? sender, KeyEventArgs e)
    {
        // The view raises this with its presentation host as the sender, and the
        // address box writes straight to the host, so the typed text is already
        // there to navigate to.
        if (e.Key != Key.Enter || sender is not BrowserPresentationHost browser)
        {
            return;
        }

        e.Handled = true;
        await RunBrowserOperationAsync(token =>
            browser.NavigateAddressAsync(browser.AddressText, token));
    }

    private async void OnBrowserBackClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.GoBackAsync);
        }
    }

    private async void OnBrowserForwardClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.GoForwardAsync);
        }
    }

    private async void OnBrowserReloadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.ReloadAsync);
        }
    }

    private async void OnBrowserStopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is BrowserPresentationHost browser)
        {
            await RunBrowserOperationAsync(browser.StopAsync);
        }
    }

    private async Task RunBrowserOperationAsync(
        Func<CancellationToken, ValueTask> operation)
    {
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnFileNavigateUpClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateUpAsync(_lifetime.Token);
        }
    }

    private async void OnFileRefreshClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnFileLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.NavigateFromTextAsync(_lifetime.Token);
            e.Handled = true;
        }
    }

    private async void OnFileEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = e;
        if (sender is ListBox
            {
                DataContext: FileRuntimePanelViewModel panel,
                SelectedItem: FileEntryViewModel entry,
            })
        {
            await panel.OpenEntryAsync(entry, _lifetime.Token);
        }
    }

    private async void OnFileEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (sender is ListBox { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.PreviewSelectedAsync(_lifetime.Token);
        }
    }

    private async void OnFileEntryTransferKeyRequested(
        object? sender,
        FilePanelTransferKeyEventArgs e)
    {
        if (sender is not ListBox { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        if (e.KeyEvent.Key is Key.C or Key.X)
        {
            if (e.Entries.Count == 0)
            {
                return;
            }

            _fileTransferClipboard = new FilePanelTransferPayload(
                panel.Id,
                e.Entries,
                e.KeyEvent.Key == Key.X
                    ? FilePanelTransferOperation.Move
                    : FilePanelTransferOperation.Copy);
            e.KeyEvent.Handled = true;
            return;
        }

        if (e.KeyEvent.Key != Key.V)
        {
            return;
        }

        e.KeyEvent.Handled = true;
        if (_fileTransferClipboard is not { } clipboard)
        {
            panel.ReportValidationError("Copy or cut a file or folder first.");
            return;
        }

        if (await QueueIncomingFileTransferAsync(panel, clipboard)
            && clipboard.Operation == FilePanelTransferOperation.Move)
        {
            _fileTransferClipboard = null;
        }
    }

    private async void OnFileEntryTransferDropRequested(
        object? sender,
        FilePanelTransferDropEventArgs e)
    {
        _ = sender;
        _ = await QueueIncomingFileTransferAsync(
            e.Destination,
            e.Payload,
            e.DestinationFolder);
    }

    private async Task<bool> QueueIncomingFileTransferAsync(
        FileRuntimePanelViewModel destination,
        FilePanelTransferPayload payload,
        FilePanelLocation? destinationFolder = null)
    {
        var queuedAll = true;
        foreach (var entry in payload.Entries)
        {
            try
            {
                var request = destination.CreateIncomingTransferRequest(
                    entry,
                    payload.Operation,
                    destinationFolder);
                queuedAll &= await ViewModel.QueueFileTransferAsync(
                    request,
                    _lifetime.Token);
            }
            catch (ArgumentException exception)
            {
                destination.ReportValidationError(exception.Message);
                queuedAll = false;
            }
            catch (InvalidOperationException exception)
            {
                destination.ReportValidationError(exception.Message);
                queuedAll = false;
            }
        }

        return queuedAll;
    }

    private async void OnFileCreateFolderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel })
        {
            return;
        }

        var name = await new FileNameDialog("New folder", "Create")
            .ShowDialog<string?>(this);
        if (name is not null)
        {
            _ = await panel.CreateFolderAsync(name, _lifetime.Token);
        }
    }

    private async void OnFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanTransfer)
        {
            return;
        }

        var request = await new FileTransferDialog(panel.CreateTransferEditor())
            .ShowDialog<FilePanelTransferRequest?>(this);
        if (request is not null)
        {
            _ = await panel.QueueTransferAsync(request, _lifetime.Token);
        }
    }

    private async void OnFileDownloadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanDownload)
        {
            return;
        }

        var request = await new FileTransferDialog(panel.CreateDownloadEditor())
            .ShowDialog<FilePanelTransferRequest?>(this);
        if (request is not null)
        {
            _ = await panel.QueueTransferAsync(request, _lifetime.Token);
        }
    }

    private async void OnFileUploadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanUpload)
        {
            return;
        }

        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload a file from Home",
            AllowMultiple = false,
        });
        var path = selected.Count == 1 ? selected[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        try
        {
            var request = await new FileTransferDialog(panel.CreateUploadEditor(path))
                .ShowDialog<FilePanelTransferRequest?>(this);
            if (request is not null)
            {
                _ = await panel.QueueTransferAsync(request, _lifetime.Token);
            }
        }
        catch (ArgumentException exception)
        {
            panel.ReportValidationError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            panel.ReportValidationError(exception.Message);
        }
    }

    private void OnFileOpenExternallyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileRuntimePanelViewModel panel }
            || !panel.CanOpenExternally)
        {
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = panel.GetSelectedLocalPath(),
                UseShellExecute = true,
            });
            if (process is null)
            {
                panel.ReportValidationError(
                    "The operating system did not provide an application for this file.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            panel.ReportValidationError(
                "The operating system could not open this file with its default application.");
        }
    }

    private async void OnFileRenameClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: FileRuntimePanelViewModel
                {
                    SelectedEntry: { } selected,
                } panel,
            })
        {
            return;
        }

        var name = await new FileNameDialog("Rename item", "Rename", selected.Name)
            .ShowDialog<string?>(this);
        if (name is not null)
        {
            _ = await panel.RenameSelectedAsync(name, _lifetime.Token);
        }
    }

    private async void OnFileDeleteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: FileRuntimePanelViewModel
                {
                    SelectedEntry: { } selected,
                } panel,
            })
        {
            return;
        }

        var kind = selected.IsDirectory ? "folder" : "file";
        var confirmed = await new FileDeleteDialog(
                kind,
                selected.Name,
                panel.SelectedProfile?.Name ?? "this provider",
                panel.SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Versioning) == true)
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            _ = await panel.DeleteSelectedAsync(_lifetime.Token);
        }
    }

    private async void OnFileLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            await panel.LoadMoreAsync(_lifetime.Token);
        }
    }

    private void OnDismissFileOperationIssueClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileRuntimePanelViewModel panel })
        {
            panel.ClearOperationIssue();
        }
    }

    private async void OnCloseRuntimeTabClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RuntimeTabViewModel tab })
        {
            return;
        }

        if (tab.Panels.All(panel => panel is FileRuntimePanelViewModel { HostedClient: null }
                or UnavailableRuntimePanelViewModel
                or TerminalRuntimePanelViewModel { SessionRequest: null }
                or StatisticsRuntimePanelViewModel { HasHostedSession: false }
                or ProcessMonitorRuntimePanelViewModel { HasHostedSession: false })
            || await RunCloseFlowAsync(
            (decision, cancellationToken) =>
                ViewModel.CloseTabAsync(tab.Id, decision, cancellationToken)))
        {
            if (await ViewModel.RemoveTabAsync(tab.Id, _lifetime.Token))
            {
                FocusActivePanel();
            }
        }
    }

    private async void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddLocalTerminalPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherConnectionViewModel connection })
        {
            return;
        }

        if (await ViewModel.AddConnectionPanelAsync(connection.Id, _lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddFilePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddFilePanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddBrowserPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddStatisticsPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private async void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (await ViewModel.AddProcessMonitorPanelAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    private sealed record RuntimeTabDragPayload(
        WindowInstanceId WindowId,
        WorkspaceInstanceId WorkspaceId,
        TabInstanceId TabId,
        string Title);

    private sealed record RuntimeTabActiveDrag(
        Control Source,
        IPointer Pointer,
        RuntimeTabDragPayload Payload);

    private sealed record RuntimeTabDragCandidate(
        Control Source,
        Point Origin,
        IPointer Pointer,
        RuntimeTabDragPayload Payload);

    private sealed record RuntimeTabDropTarget(
        Grid Target,
        TabInstanceId AnchorTabId,
        RuntimeTabPlacement Placement);

    private async Task CloseActivePanelAsync()
    {
        if (ViewModel.ActivePanel is not { } panel)
        {
            return;
        }

        if (await CloseRuntimePanelAsync(panel))
        {
            _ = await ViewModel.RemovePanelAsync(panel.Id, _lifetime.Token);
        }
    }

    private Task<bool> CloseRuntimePanelAsync(RuntimePanelViewModel panel) => panel switch
    {
        UnavailableRuntimePanelViewModel => Task.FromResult(true),
        FileRuntimePanelViewModel { HostedClient: null } => Task.FromResult(true),
        TerminalRuntimePanelViewModel { SessionRequest: null } => Task.FromResult(true),
        StatisticsRuntimePanelViewModel { HasHostedSession: false } => Task.FromResult(true),
        ProcessMonitorRuntimePanelViewModel { HasHostedSession: false } => Task.FromResult(true),
        FileRuntimePanelViewModel filePanel => RunCloseFlowAsync((decision, token) =>
            ViewModel.CloseFilePanelAsync(filePanel, decision, token)),
        _ => RunCloseFlowAsync((decision, token) =>
            ViewModel.ClosePanelAsync(panel.Id, decision, token)),
    };

    private async Task CloseActiveTabAsync()
    {
        if (ViewModel.RuntimeWorkspace?.ActiveTab is { } tab
            && await RunCloseFlowAsync((decision, token) =>
                ViewModel.CloseTabAsync(tab.Id, decision, token)))
        {
            _ = await ViewModel.RemoveTabAsync(tab.Id, _lifetime.Token);
        }
    }

    private async Task RenameActiveTabAsync()
    {
        if (ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
        {
            return;
        }

        var title = await new FileNameDialog(
            "Rename tab",
            "Rename",
            tab.Title,
            "This name identifies the live tab; saved screen definitions are unchanged.")
            .ShowDialog<string?>(this);
        if (title is not null)
        {
            _ = await ViewModel.RenameActiveTabAsync(title, _lifetime.Token);
        }
    }

    private void FocusActivePanel()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.ActivePanel is not { } activePanel)
            {
                return;
            }

            var terminal = FindActiveTerminalHost();
            if (terminal is not null)
            {
                terminal.RequestInputFocus();
                return;
            }

            var browser = this.GetVisualDescendants()
                .OfType<BrowserPresentationHost>()
                .FirstOrDefault(control =>
                    ReferenceEquals(control.DataContext, activePanel));
            if (browser is not null)
            {
                browser.RequestInputFocus();
                return;
            }

            // Reaching here for a terminal panel means the host lookup above failed
            // to recognise its own surface, and focus is about to be handed to a
            // plain Avalonia control instead. On macOS that makes the native view
            // resign first responder, and the terminal stops accepting keystrokes
            // while still drawing output — so say so rather than fail silently.
            if (activePanel is TerminalRuntimePanelViewModel)
            {
                Console.Error.WriteLine(
                    "[ghostshell:input] focus fell back to a non-terminal control for "
                    + $"panel {activePanel.Id}; its terminal surface was not found in "
                    + "the visual tree, so the keyboard has left the terminal");
            }

            this.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control =>
                    ReferenceEquals(control.DataContext, activePanel)
                    && control.Classes.Contains("RuntimePanelFocusTarget"))
                ?.Focus();
        });
    }

    private TerminalPresentationHost? FindActiveTerminalHost()
    {
        var activePanel = ViewModel.ActivePanel;
        return activePanel is null
            ? null
            : this.GetVisualDescendants()
                .OfType<TerminalPresentationHost>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, activePanel));
    }
}
