using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    private const double RuntimeTabDragThreshold = 6;
    private static readonly DataFormat<RuntimeTabDragPayload> RuntimeTabDragFormat =
        DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>(
            "app.ghostshell.runtime-tab");

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
                item.CanOpen && item.Kind == "LOCAL")
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
            e,
            point.Position,
            e.Pointer,
            new RuntimeTabDragPayload(
                ViewModel.WindowId,
                workspace.Id,
                tab.Id));
        e.Handled = true;
    }

    private async void OnRuntimeTabDragPointerMoved(
        object? sender,
        PointerEventArgs e)
    {
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
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(
            RuntimeTabDragFormat,
            candidate.Payload));
        try
        {
            _ = await DragDrop.DoDragDropAsync(
                candidate.TriggerEvent,
                transfer,
                DragDropEffects.Move);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            ViewModel.SetError("The tab drag could not start.");
        }
        finally
        {
            _runtimeTabDragInProgress = false;
            ClearRuntimeTabDropIndicator();
        }
    }

    private void OnRuntimeTabDragPointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        _ = sender;
        _ = e;
        _runtimeTabDragCandidate = null;
    }

    private void OnRuntimeTabDragPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        _ = e;
        _runtimeTabDragCandidate = null;
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
            || ViewModel.RuntimeWorkspace is not { } workspace
            || candidate.WindowId != ViewModel.WindowId
            || candidate.WorkspaceId != workspace.Id
            || candidate.TabId == targetTab.Id
            || workspace.Tabs.All(tab => tab.Id != candidate.TabId))
        {
            return false;
        }

        placement = e.GetPosition(target).X < target.Bounds.Width / 2
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

        payload = candidate;
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

    /// <summary>
    /// The terminal surface took focus natively. Avalonia never sees that, so
    /// this is the only signal that the keyboard has moved into this panel.
    /// </summary>
    private async void OnRuntimePanelTerminalFocused(object? sender, RoutedEventArgs e)
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
            // Selection follows input focus. Do not move focus here: the native or managed
            // terminal surface that raised the event must keep keyboard ownership.
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
        if (sender is not Control { DataContext: PanelPlaceholderViewModel placeholder }
            || ViewModel.RuntimeWorkspace?.ActiveTab is not { } tab)
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

    private async void OnFileProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (sender is ComboBox
            {
                DataContext: FileRuntimePanelViewModel panel,
                SelectedItem: FileProviderProfileDescriptor profile,
            }
            && panel.SelectedProfile?.Id != profile.Id)
        {
            await panel.SelectProfileAsync(profile, _lifetime.Token);
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
        TabInstanceId TabId);

    private sealed record RuntimeTabDragCandidate(
        Control Source,
        PointerPressedEventArgs TriggerEvent,
        Point Origin,
        IPointer Pointer,
        RuntimeTabDragPayload Payload);

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
