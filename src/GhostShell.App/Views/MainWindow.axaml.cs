using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.App.Controls;
using GhostShell.App.Views.Overlays;
using GhostShell.Application;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private ApplicationKeySequenceResolver _applicationKeys = new(
        BuiltInKeymaps.TmuxApplication);
    private KeymapProfileId _activeApplicationKeymapId = BuiltInKeymaps.TmuxApplicationId;
    private long _activeApplicationKeymapRevision;
    private CancellationTokenSource? _applicationHintLifetime;
    private CancellationTokenSource? _historyExportLifetime;
    private DefinitionBundleController? _definitionBundles;
    private RecentSessionHistoryExportController? _historyExport;
    private bool _closeApproved;
    private bool _closeInProgress;
    private bool _restoreRouteFocusWhenActivated;

    public MainWindow()
    {
        InitializeComponent();
        SettingsRoute.ConfigureAppearanceControls(
            AppearancePlatformProfiles,
            AppearanceTextScaleOptions);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Activated += OnWindowActivated;
    }

    public MainWindow(
        IDefinitionBundleStore definitionBundleStore,
        IDefinitionCatalog definitionCatalog,
        IDiagnosticsBundleExporter diagnosticsExporter,
        IDiagnosticsBundleRequestSource diagnosticsRequestSource,
        IDiagnosticsArtifactPresenter diagnosticsArtifactPresenter,
        IRecentSessionHistoryExporter recentSessionHistoryExporter,
        RecoveryDataControlViewModel recoveryDataControlViewModel,
        LocalArtifactControlViewModel localArtifactControlViewModel)
        : this()
    {
        _definitionBundles = new DefinitionBundleController(
            definitionBundleStore,
            new AvaloniaDefinitionBundlePathPicker(this),
            new DefinitionCatalogImportRefresh(definitionCatalog));
        _historyExport = new RecentSessionHistoryExportController(
            recentSessionHistoryExporter,
            new AvaloniaRecentSessionHistoryPathPicker(this));
        var diagnosticsExportViewModel = new DiagnosticsExportViewModel(
            diagnosticsExporter,
            diagnosticsRequestSource,
            new AvaloniaDiagnosticsBundleDestinationPicker(this),
            diagnosticsArtifactPresenter,
            TimeProvider.System);
        SettingsRoute.BindOperationalViewModels(
            recoveryDataControlViewModel,
            localArtifactControlViewModel,
            diagnosticsExportViewModel);
        recoveryDataControlViewModel.Start();
        localArtifactControlViewModel.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeApproved)
        {
            e.Cancel = true;
            if (!_closeInProgress)
            {
                _ = CloseWindowAsync();
            }
        }

        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshAppearanceControlsFromStoredProfile();
    }

    protected override void OnClosed(EventArgs e)
    {
        _applicationHintLifetime?.Cancel();
        _applicationHintLifetime?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private MainWindowViewModel ViewModel => DataContext as MainWindowViewModel
        ?? throw new InvalidOperationException("The main window view model is unavailable.");

    private CommandPaletteView CommandPaletteOverlay =>
        this.FindControl<CommandPaletteView>("CommandPaletteOverlayView")
        ?? throw new InvalidOperationException(
            "The command palette overlay view is unavailable.");

    private LayoutDesignerView LayoutDesignerOverlay =>
        this.FindControl<LayoutDesignerView>("LayoutDesignerOverlayView")
        ?? throw new InvalidOperationException(
            "The layout designer overlay view is unavailable.");

    private NewItemLauncherView NewItemLauncherOverlay =>
        this.FindControl<NewItemLauncherView>("NewItemLauncherOverlayView")
        ?? throw new InvalidOperationException(
            "The new item launcher overlay view is unavailable.");

    private NewPanelChooserView NewPanelChooserOverlay =>
        this.FindControl<NewPanelChooserView>("NewPanelChooserOverlayView")
        ?? throw new InvalidOperationException(
            "The new panel chooser overlay view is unavailable.");

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (!e.Pointer.IsPrimary)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        // Avalonia's native TitleBar role is not consistently honored on macOS.
        // Keep the role for native hit-testing and use this client event as a fallback.
        BeginMoveDrag(e);
        e.Handled = true;
    }

    public void ShowCommandPalette()
    {
        ViewModel.ShowOverlay(ShellOverlay.CommandPalette);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ViewModel.IsCommandPaletteVisible)
            {
                return;
            }

            CommandPaletteOverlay.FocusSearch();
            ViewModel.SelectFirstAvailableLauncherSearchResult();
        });
    }

    public void ShowNewItemLauncher()
    {
        ViewModel.ShowOverlay(ShellOverlay.NewItem);
        FocusNewTerminalButton();
        Avalonia.Threading.DispatcherTimer.RunOnce(
            FocusNewTerminalButton,
            TimeSpan.FromMilliseconds(100));
    }

    private void FocusNewTerminalButton()
    {
        if (ViewModel.IsNewItemVisible)
        {
            NewItemLauncherOverlay.FocusInitialAction();
        }
    }

    public void NavigateToLauncher()
    {
        ViewModel.ShowLauncher();
        if (ViewModel.IsLauncherVisible && !ViewModel.HasOverlay)
        {
            FocusLauncherWhenReady(static launcher =>
                launcher.FocusHomeNavigation());
        }
    }


    public async Task ShowNewPanelChooserAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace tab before adding a panel.");
            return;
        }

        ViewModel.ShowWorkspace();
        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        ViewModel.ShowOverlay(ShellOverlay.NewPanel);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.IsNewPanelVisible)
            {
                NewPanelChooserOverlay.FocusInitialAction();
            }
        });
    }

    public async Task ShowLayoutDesignerAsync()
    {
        if (ViewModel.IsLayoutDesignerVisible)
        {
            FocusLayoutDesignerNameWhenReady();
            return;
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        ViewModel.BeginCreateLayout();
        FocusLayoutDesignerNameWhenReady();
    }


    internal static bool IsExactGlobalGesture(
        Key actualKey,
        AvaloniaKeyModifiers actualModifiers,
        Key expectedKey,
        AvaloniaKeyModifiers commandModifier) =>
        actualKey == expectedKey && actualModifiers == commandModifier;

    private void OnShowLauncherClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToLauncher();
    }

    private void OnLauncherHomeClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ShowLauncherOverview();
        if (!ViewModel.IsLauncherOverviewVisible)
        {
            return;
        }

        FocusLauncherOverviewSection(
            LauncherOverviewSection.Home,
            resetScroll: true);
    }

    private void OnLauncherConnectionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ShowLauncherOverview();
        FocusLauncherOverviewSection(LauncherOverviewSection.Connections);
    }

    private void OnLauncherScreensClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ShowLauncherOverview();
        FocusLauncherOverviewSection(LauncherOverviewSection.Screens);
    }

    private void OnLauncherHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ShowLauncherHistory();
        if (ViewModel.IsLauncherHistoryVisible)
        {
            FocusLauncherWhenReady(static launcher =>
                launcher.FocusHistorySearch());
        }
    }

    private void FocusLauncherOverviewSection(
        LauncherOverviewSection section,
        bool resetScroll = false) =>
        this.FindControl<LauncherView>("LauncherRouteView")
            ?.FocusOverviewSection(section, resetScroll);

    private async void OnHistorySearchKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter
            && ViewModel.SelectedHistorySession is { CanOpen: true } selected)
        {
            e.Handled = true;
            await ReplaceRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(selected, token));
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (!string.IsNullOrEmpty(ViewModel.HistorySearchQuery))
        {
            ViewModel.HistorySearchQuery = string.Empty;
            return;
        }

        ViewModel.ShowLauncherOverview();
        FocusLauncherWhenReady(static launcher =>
            launcher.FocusHistoryNavigation());
    }

    private void OnShowSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToSettings();
    }

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToSettings(SettingsPage.Agent);
    }

    private async void OnExportDefinitionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_definitionBundles is null)
        {
            ViewModel.SetError("Definition portability is unavailable in this host.");
            return;
        }

        ViewModel.ClearError();
        var result = await _definitionBundles.ExportAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            var receipt = result.Value!;
            ViewModel.SetDefinitionBundleStatus(
                $"Exported {receipt.DefinitionCount} definitions to {Path.GetFileName(receipt.Path)}.");
        }
        else if (result.Error!.Code != DefinitionStoreErrorCode.Cancelled)
        {
            ViewModel.SetError(result.Error.Message);
        }
    }

    private async void OnImportDefinitionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_definitionBundles is null)
        {
            ViewModel.SetError("Definition portability is unavailable in this host.");
            return;
        }

        var mode = await new DefinitionImportModeDialog()
            .ShowDialog<DefinitionImportMode?>(this);
        if (mode is null)
        {
            return;
        }

        ViewModel.ClearError();
        var preflight = await _definitionBundles.PreflightImportAsync(
            mode.Value,
            _lifetime.Token);
        if (!preflight.IsSuccess)
        {
            if (preflight.Error!.Code != DefinitionStoreErrorCode.Cancelled)
            {
                ViewModel.SetError(preflight.Error.Message);
            }

            return;
        }

        var plan = preflight.Value!;
        var confirmed = await new DefinitionImportPreflightDialog(plan)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        var applied = await _definitionBundles.ConfirmAndApplyImportAsync(
            plan,
            _lifetime.Token);
        if (!applied.IsSuccess)
        {
            if (applied.Error!.Code != DefinitionStoreErrorCode.Cancelled)
            {
                ViewModel.SetError(applied.Error.Message);
            }

            return;
        }

        var receipt = applied.Value!;
        ViewModel.SetDefinitionBundleStatus(
            receipt.CatalogReloaded
                ? $"Imported {receipt.Inserted} new and replaced {receipt.Replaced} definitions."
                : $"Imported {receipt.Inserted} new and replaced {receipt.Replaced} definitions, but the catalog refresh failed.");
        if (!receipt.CatalogReloaded)
        {
            ViewModel.SetError(receipt.ReloadError!.Message);
        }

        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnRetryOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnFinishOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.CompleteAsync(_lifetime.Token);
        }
    }

    private void OnReviewOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToLauncher();
        ViewModel.Onboarding?.ShowReview();
        FocusLauncherWhenReady(static launcher =>
            launcher.FocusOnboardingFinish());
    }

    private void OnReviewHistoryPrivacyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ShowLauncherHistory();
        if (ViewModel.IsLauncherHistoryVisible)
        {
            FocusLauncherWhenReady(static launcher =>
                launcher.FocusHistorySearch());
        }
    }


    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowCommandPalette();
    }

    private void OnShowNewItemClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowNewItemLauncher();
    }

    private async void OnShowNewPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowNewPanelChooserAsync();
    }

    private async void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowLayoutDesignerAsync();
    }

    private async void OnCloseOverlayClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await TryCloseOverlayAsync();
    }

    private async Task<bool> TryCloseOverlayAsync()
    {
        if (ViewModel.IsLayoutDesignerVisible
            && ViewModel.LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog().ShowDialog<bool>(this))
        {
            return false;
        }

        if (ViewModel.IsDefinitionEditorVisible
            && ViewModel.WorkspaceEditor?.RequestCancel()
                == WorkspaceEditorCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog(
                    "Discard workspace changes?",
                    "The unsaved workspace order, tabs, panels, and startup settings will be lost.")
                .ShowDialog<bool>(this))
        {
            return false;
        }

        if (ViewModel.IsLayoutDesignerVisible)
        {
            ViewModel.DismissLayoutDesigner();
        }
        else if (ViewModel.IsDefinitionEditorVisible)
        {
            ViewModel.DismissWorkspaceEditor();
        }
        else
        {
            ViewModel.CloseOverlay();
        }

        FocusCurrentRoute();
        return true;
    }

    private async void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            await ReplaceRuntimeWorkspaceAsync(token =>
                ViewModel.OpenWorkspaceAsync(workspace.Id, token));
        }
    }

    private async void OnOpenConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherConnectionViewModel connection })
        {
            await LaunchConnectionTargetAsync(connection.Id);
        }
    }

    private async Task LaunchConnectionTargetAsync(ConnectionId connectionId)
    {
        try
        {
            if (await ViewModel.LaunchConnectionAsync(connectionId, _lifetime.Token)
                && ViewModel.Overlay == ShellOverlay.None
                && ViewModel.Route == ShellRoute.Workspace)
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnNewLocalTerminalClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewTerminalAsync();
    }

    private async void OnNewFileViewerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewFileViewerAsync();
    }

    private async void OnNewBrowserClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewBrowserAsync();
    }

    private async void OnNewStatisticsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewStatisticsAsync();
    }

    private async void OnNewProcessMonitorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewProcessMonitorAsync();
    }

    private async void OnAddConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowConnectionEditorAsync(null);
    }

    private async void OnEditConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherConnectionViewModel connection })
        {
            await ShowConnectionEditorAsync(connection.Id);
        }
    }

    private async void OnDeleteConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherConnectionViewModel connection })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("connection", connection.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(ConnectionProfile.Kind, connection.Id.Value),
            connection.Revision,
            _lifetime.Token);
    }

    private async Task ShowConnectionEditorAsync(ConnectionId? connectionId)
    {
        try
        {
            ViewModel.CloseOverlay();
            var editor = ViewModel.CreateConnectionEditor(connectionId);
            var request = await new ConnectionEditorDialog(editor)
                .ShowDialog<ConnectionEditorSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveConnectionAsync(request, _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnOpenScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            await LaunchScreenTargetAsync(screen.Id);
        }
    }

    private async Task LaunchScreenTargetAsync(ScreenId screenId)
    {
        try
        {
            if (await ViewModel.LaunchScreenAsync(screenId, _lifetime.Token)
                && ViewModel.Overlay == ShellOverlay.None
                && ViewModel.Route == ShellRoute.Workspace)
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnOpenRecentSessionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: RecentSessionHistoryItemViewModel recentSession })
        {
            await ReplaceRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(recentSession, token));
        }
    }

    private async void OnClearRecentSessionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var cutoff = await new HistoryClearDialog(ViewModel.CaptureRecentSessionClearCutoff)
            .ShowDialog<RecentSessionClearCutoff?>(this);
        if (cutoff is null)
        {
            return;
        }

        _ = await ViewModel.ClearRecentSessionsAsync(cutoff, _lifetime.Token);
    }

    private async void OnOpenSelectedHistorySessionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.SelectedHistorySession is { CanOpen: true } recentSession)
        {
            await ReplaceRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(recentSession, token));
        }
    }

    private async void OnResetRecentSessionHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!ViewModel.CanResetRecentSessionHistory
            || !await new HistoryResetDialog().ShowDialog<bool>(this))
        {
            return;
        }

        _ = await ViewModel.ResetUnreadableRecentSessionsAsync(_lifetime.Token);
    }

    private async void OnRetryRecentSessionHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.RetryRecentSessionHistoryAsync(_lifetime.Token);
    }

    private async void OnSaveHistoryRetentionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.SelectedHistoryRetentionOption is not { } selected)
        {
            return;
        }

        if (ViewModel.RequiresHistoryRetentionConfirmation
            && !await new HistoryRetentionChangeDialog(selected).ShowDialog<bool>(this))
        {
            return;
        }

        _ = await ViewModel.SaveHistoryRetentionAsync(_lifetime.Token);
    }

    private async void OnExportAllHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ExportHistoryAsync(HistoryExportScope.AllRetained);
    }

    private async void OnExportFilteredHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ExportHistoryAsync(HistoryExportScope.CurrentResults);
    }

    private async Task ExportHistoryAsync(HistoryExportScope scope)
    {
        if (_historyExport is null)
        {
            ViewModel.SetHistoryExportStatus("Session-history export is unavailable.");
            return;
        }

        if (!ViewModel.TryBeginHistoryExport(scope))
        {
            return;
        }

        using var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _historyExportLifetime = exportCancellation;
        var finalStatus = "Session-history export failed. Please choose a different destination and retry.";
        try
        {
            var snapshot = ViewModel.CaptureHistoryExportSnapshot();
            if (snapshot.Count == 0)
            {
                finalStatus = "There are no matching metadata records to export.";
                return;
            }

            var result = await _historyExport.ExportAsync(
                snapshot,
                exportCancellation.Token);
            if (!result.IsSuccess)
            {
                finalStatus = result.Error!.Code == RecentSessionHistoryExportErrorCode.Cancelled
                    ? "Session-history export cancelled."
                    : $"{result.Error.Message} Choose a different destination and retry.";
                return;
            }

            finalStatus =
                $"Exported {result.Value!.Export.RecordCount:N0} metadata-only records to {Path.GetFileName(result.Value.Path)}.";
        }
        catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested)
        {
            finalStatus = "Session-history export cancelled.";
        }
        catch (Exception)
        {
            finalStatus =
                "Session-history export failed unexpectedly. Choose a different destination and retry.";
        }
        finally
        {
            _historyExportLifetime = null;
            ViewModel.EndHistoryExport(finalStatus);
        }
    }

    private void OnCancelHistoryExportClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _historyExportLifetime?.Cancel();
    }

    private async void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var result = await ViewModel.CreateWorkspaceAsync(
            NewItemLauncherOverlay.WorkspaceName,
            _lifetime.Token);
        if (result.IsSuccess)
        {
            NewItemLauncherOverlay.ClearWorkspaceName();
            ViewModel.CloseOverlay();
            FocusCurrentRoute();
        }
    }

    private async void OnCreateScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            var editor = ViewModel.CreateNewSavedScreenEditor(
                NewItemLauncherOverlay.ScreenName);
            var saved = await new SavedScreenEditorDialog(
                    editor,
                    ViewModel.SaveSavedScreenAsync)
                .ShowDialog<bool>(this);
            if (!saved)
            {
                return;
            }

            NewItemLauncherOverlay.ClearScreenName();
            ViewModel.CloseOverlay();
            FocusCurrentRoute();
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnSaveLayoutDesignerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var result = await ViewModel.SaveLayoutDesignerAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            ViewModel.DismissLayoutDesigner();
            FocusCurrentRoute();
        }
    }

    private async void OnEditLayoutClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LayoutCardViewModel layout })
        {
            return;
        }

        if (ViewModel.LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog().ShowDialog<bool>(this))
        {
            return;
        }

        ViewModel.DismissLayoutDesigner();
        ViewModel.BeginEditLayout(layout.Id);
        FocusLayoutDesignerNameWhenReady();
    }

    private void OnLayoutGridSizeChanged(
        object? sender,
        NumericUpDownValueChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var editor = ViewModel.LayoutDesignerEditor;
        var gridSize = LayoutDesignerOverlay.CaptureGridSize();
        if (editor is null
            || gridSize.Rows is null
            || gridSize.Columns is null)
        {
            return;
        }

        _ = editor.ResizeGrid(
            (int)gridSize.Rows.Value,
            (int)gridSize.Columns.Value);
    }

    private void OnLayoutSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LayoutDesignerSlotViewModel slot })
        {
            _ = ViewModel.LayoutDesignerEditor?.SelectSlot(slot.Id);
        }
    }

    private void OnLayoutMoveLeftClick(object? sender, RoutedEventArgs e) =>
        MoveLayoutSelection(sender, e, LayoutDesignerDirection.Left);

    private void OnLayoutMoveRightClick(object? sender, RoutedEventArgs e) =>
        MoveLayoutSelection(sender, e, LayoutDesignerDirection.Right);

    private void OnLayoutMoveUpClick(object? sender, RoutedEventArgs e) =>
        MoveLayoutSelection(sender, e, LayoutDesignerDirection.Up);

    private void OnLayoutMoveDownClick(object? sender, RoutedEventArgs e) =>
        MoveLayoutSelection(sender, e, LayoutDesignerDirection.Down);

    private void OnLayoutGrowLeftClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Left, 1);

    private void OnLayoutGrowRightClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Right, 1);

    private void OnLayoutGrowTopClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Top, 1);

    private void OnLayoutGrowBottomClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Bottom, 1);

    private void OnLayoutShrinkLeftClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Left, -1);

    private void OnLayoutShrinkRightClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Right, -1);

    private void OnLayoutShrinkTopClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Top, -1);

    private void OnLayoutShrinkBottomClick(object? sender, RoutedEventArgs e) =>
        ResizeLayoutSelection(sender, e, LayoutDesignerEdge.Bottom, -1);

    private void OnLayoutMoveEarlierClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.MoveSelectedEarlier();
    }

    private void OnLayoutMoveLaterClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.MoveSelectedLater();
    }

    private void OnLayoutAddSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.AddSlot();
    }

    private void OnLayoutTogglePaintModeClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var editor = ViewModel.LayoutDesignerEditor;
        if (editor is null || !editor.TogglePaintMode().IsSuccess)
        {
            return;
        }

        if (editor.IsPaintMode)
        {
            LayoutDesignerOverlay.FocusGrid();
        }
    }

    private void OnLayoutRemoveSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.RemoveSelectedSlot();
    }

    private void OnResetLayoutClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.LayoutDesignerEditor?.Reset();
    }

    private void OnLayoutDesignerKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        var editor = ViewModel.LayoutDesignerEditor;
        if (editor is null)
        {
            return;
        }

        if (e.Key is Key.PageDown or Key.PageUp)
        {
            _ = e.Key == Key.PageDown
                ? editor.SelectNextSlot()
                : editor.SelectPreviousSlot();
            FocusSelectedLayoutSlot();
            e.Handled = true;
            return;
        }

        if (!TryGetLayoutDirection(e.Key, out var direction, out var edge))
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(AvaloniaKeyModifiers.Alt))
        {
            _ = editor.MoveSelected(direction);
        }
        else if (e.KeyModifiers.HasFlag(AvaloniaKeyModifiers.Shift))
        {
            _ = editor.ResizeSelected(edge, 1);
        }
        else if (e.KeyModifiers.HasFlag(AvaloniaKeyModifiers.Control))
        {
            _ = editor.ResizeSelected(edge, -1);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void MoveLayoutSelection(
        object? sender,
        RoutedEventArgs e,
        LayoutDesignerDirection direction)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.MoveSelected(direction);
    }

    private void ResizeLayoutSelection(
        object? sender,
        RoutedEventArgs e,
        LayoutDesignerEdge edge,
        int units)
    {
        _ = sender;
        _ = e;
        _ = ViewModel.LayoutDesignerEditor?.ResizeSelected(edge, units);
    }

    private static bool TryGetLayoutDirection(
        Key key,
        out LayoutDesignerDirection direction,
        out LayoutDesignerEdge edge)
    {
        (direction, edge) = key switch
        {
            Key.Left => (LayoutDesignerDirection.Left, LayoutDesignerEdge.Left),
            Key.Right => (LayoutDesignerDirection.Right, LayoutDesignerEdge.Right),
            Key.Up => (LayoutDesignerDirection.Up, LayoutDesignerEdge.Top),
            Key.Down => (LayoutDesignerDirection.Down, LayoutDesignerEdge.Bottom),
            _ => default,
        };
        return key is Key.Left or Key.Right or Key.Up or Key.Down;
    }

    private void OnEditWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            ViewModel.BeginEditWorkspace(workspace.Id);
            FocusDefinitionEditorWhenReady();
        }
    }

    private async void OnEditScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            try
            {
                var editor = ViewModel.CreateSavedScreenEditor(screen.Id);
                _ = await new SavedScreenEditorDialog(
                        editor,
                        ViewModel.SaveSavedScreenAsync)
                    .ShowDialog<bool>(this);
            }
            catch (InvalidOperationException exception)
            {
                ViewModel.SetError(exception.Message);
            }
        }
    }

    private async void OnSaveDefinitionEditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.WorkspaceEditor is not null)
        {
            _ = await ViewModel.SaveWorkspaceEditorAsync(_lifetime.Token);
            return;
        }

        _ = await ViewModel.SaveDefinitionEditAsync(_lifetime.Token);
    }

    private async void OnWorkspaceEditorSaveRequested(
        object? sender,
        WorkspaceEditorSaveRequestedEventArgs e)
    {
        _ = sender;
        var result = await ViewModel.SaveWorkspaceEditorAsync(e.Request, _lifetime.Token);
        if (result.IsSuccess)
        {
            FocusCurrentRoute();
        }
    }

    private async void OnWorkspaceEditorCancelRequested(
        object? sender,
        WorkspaceEditorCancelRequestedEventArgs e)
    {
        _ = sender;
        if (e.Disposition == WorkspaceEditorCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog(
                    "Discard workspace changes?",
                    "The unsaved workspace order, tabs, panels, and startup settings will be lost.")
                .ShowDialog<bool>(this))
        {
            return;
        }

        ViewModel.DismissWorkspaceEditor();
        FocusCurrentRoute();
    }

    private async void OnDeleteWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            var key = new DefinitionKey(WorkspaceDefinition.Kind, workspace.Id.Value);
            var dialog = ViewModel.IsDefinitionOpen(key)
                ? new DefinitionDeleteDialog(
                    "Delete the open workspace definition?",
                    $"“{workspace.Name}” is currently open. Its running tabs and sessions will remain alive, but this saved workspace can no longer be reopened after they close.",
                    "Close this dialog if you want to keep the definition or save a replacement before deleting it.",
                    "Delete and keep running")
                : new DefinitionDeleteDialog("workspace", workspace.Name);
            var confirmed = await dialog
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            _ = await ViewModel.DeleteAsync(
                key,
                workspace.Revision,
                _lifetime.Token);
        }
    }

    private async void OnDeleteScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            var confirmed = await new DefinitionDeleteDialog("saved screen", screen.Name)
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            var result = await ViewModel.DeleteSavedScreenAsync(
                new DefinitionKey(ScreenDefinition.Kind, screen.Id.Value),
                screen.Revision,
                _lifetime.Token);
            if (result.IsSuccess)
            {
                FocusSettingsWhenReady(static settings =>
                    settings.FocusSavedScreenUndo());
            }
        }
    }

    private async void OnUndoDeletedSavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var result = await ViewModel.UndoSavedScreenDeleteAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            FocusCurrentRoute();
        }
    }

    private void OnDismissSavedScreenDeleteUndoClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.DismissSavedScreenDeleteUndo();
        FocusCurrentRoute();
    }

    private void OnClearErrorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ClearError();
    }

    private async void OnLauncherSearchResultClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherSearchResultViewModel item }
            || !item.IsAvailable)
        {
            return;
        }

        await ExecuteLauncherSearchTargetAsync(item.Target);
    }

    private async Task ExecuteLauncherSearchTargetAsync(LauncherSearchTarget target)
    {
        switch (target)
        {
            case LauncherSearchTarget.CreatePanel createPanel:
                await ExecuteCreatePanelTargetAsync(createPanel.Kind);
                break;
            case LauncherSearchTarget.Command command:
                await ExecuteCommandPaletteCommandAsync(command);
                break;
            case LauncherSearchTarget.Connection connection:
                await LaunchConnectionTargetAsync(connection.Id);
                break;
            case LauncherSearchTarget.Screen screen:
                await LaunchScreenTargetAsync(screen.Id);
                break;
            case LauncherSearchTarget.Workspace workspace:
                await ReplaceRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
                break;
            case LauncherSearchTarget.RecentSession recent:
                var recentSession = ViewModel.HistorySessions.FirstOrDefault(item =>
                    item.SessionId == recent.Id);
                if (recentSession is null)
                {
                    ViewModel.SetError("That recent session is no longer available.");
                    return;
                }

                await ReplaceRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenRecentSessionAsync(recentSession, token));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private async Task ExecuteCreatePanelTargetAsync(PanelKind kind)
    {
        switch (kind)
        {
            case PanelKind.Terminal:
                await RequestNewTerminalAsync();
                break;
            case PanelKind.FileViewer:
                await RequestNewFileViewerAsync();
                break;
            case PanelKind.Browser:
                await RequestNewBrowserAsync();
                break;
            case PanelKind.Statistics:
                await RequestNewStatisticsAsync();
                break;
            case PanelKind.ProcessMonitor:
                await RequestNewProcessMonitorAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private async Task ExecuteCommandPaletteCommandAsync(
        LauncherSearchTarget.Command command)
    {
        if (command.Id == BuiltInCommands.NewTab)
        {
            await RequestNewTerminalAsync();
            return;
        }

        ViewModel.CloseOverlay();
        await ExecuteCommandAsync(command.Id, command.Arguments);
    }

    public Task ExecuteCommandAsync(CommandId commandId) =>
        ExecuteCommandAsync(commandId, EmptyCommandArguments.Instance);

    private Task ExecuteCommandAsync(CommandBinding binding) =>
        ExecuteCommandAsync(binding.CommandId, binding.Arguments);

    private async Task ExecuteCommandAsync(
        CommandId commandId,
        IReadOnlyDictionary<string, string> arguments)
    {
        var routed = ApplicationCommandRouter.Route(
            commandId,
            arguments,
            ViewModel.ActiveCommandContexts);
        if (routed.Action is not { } action)
        {
            ViewModel.SetError(routed.Error ?? "That command is unavailable.");
            return;
        }

        try
        {
            switch (action.Kind)
            {
                case ApplicationCommandActionKind.NewTab:
                    if (await ViewModel.AddLocalTerminalTabAsync(_lifetime.Token))
                    {
                        FocusActivePanel();
                    }
                    break;
                case ApplicationCommandActionKind.SplitPanel:
                    if (await ViewModel.AddLocalTerminalPanelAsync(
                        action.SplitOrientation!.Value,
                        _lifetime.Token))
                    {
                        FocusActivePanel();
                    }
                    break;
                case ApplicationCommandActionKind.FocusPanel:
                    _ = await ViewModel.FocusPanelAsync(
                        action.FocusDirection!.Value,
                        _lifetime.Token);
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.TogglePanelZoom:
                    _ = ViewModel.ToggleActivePanelZoom();
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.ClosePanel:
                    await CloseActivePanelAsync();
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.RenameTab:
                    await RenameActiveTabAsync();
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.CloseTab:
                    await CloseActiveTabAsync();
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.MoveTab:
                    _ = await ViewModel.MoveActiveTabAsync(
                        action.TabOffset!.Value,
                        _lifetime.Token);
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.SelectRelativeTab:
                    _ = await ViewModel.SelectRelativeTabAsync(
                        action.TabOffset!.Value,
                        _lifetime.Token);
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.SelectLastTab:
                    _ = await ViewModel.SelectLastActiveTabAsync(_lifetime.Token);
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.SelectTab:
                    if (!await ViewModel.SelectTabAtPositionAsync(
                            action.TabPosition!.Value,
                            _lifetime.Token))
                    {
                        ViewModel.SetError($"Tab position {action.TabPosition.Value} is not open.");
                    }

                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.EnterTerminalCopyMode:
                    _ = ViewModel.EnterTerminalCopyMode();
                    FocusActivePanel();
                    break;
                case ApplicationCommandActionKind.SendPrefix:
                    await SendLiteralPrefixAsync();
                    FocusActivePanel();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
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

    private async Task SendLiteralPrefixAsync()
    {
        var terminal = FindActiveTerminalHost();
        if (terminal is null)
        {
            ViewModel.SetError("The active panel cannot receive terminal input.");
            return;
        }

        await terminal.SendTextAsync("\u0002", _lifetime.Token);
    }

    private async void OnCommandSearchKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            _ = await TryCloseOverlayAsync();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            ViewModel.MoveLauncherSearchSelection(e.Key == Key.Down ? 1 : -1);
            CommandPaletteOverlay.ScrollSelectedResultIntoView();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (ViewModel.ConfirmLauncherSearchSelection() is { } target)
            {
                await ExecuteLauncherSearchTargetAsync(target);
            }

            e.Handled = true;
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (ViewModel.IsWorkspaceVisible && !ViewModel.HasOverlay)
        {
            SynchronizeApplicationKeymap();
            var resolution = _applicationKeys.Resolve(
                ApplicationKeyStrokeMapper.Map(e.Key, e.KeyModifiers, e.KeySymbol),
                ViewModel.ActiveCommandContexts,
                DateTimeOffset.UtcNow);
            if (resolution.Kind != ApplicationKeyResolutionKind.NotHandled)
            {
                e.Handled = resolution.ShouldHandle;
                await ApplyApplicationKeyResolutionAsync(
                    resolution,
                    FindActiveTerminalHost());
                return;
            }
        }
        else
        {
            _applicationKeys.Reset();
            ClearApplicationKeySequenceHint();
        }

        if (e.Key == Key.Escape && ViewModel.IsLayoutDesignerVisible)
        {
            var cancelledGesture = LayoutDesignerOverlay.CancelPointerGesture();
            var editor = ViewModel.LayoutDesignerEditor;
            var cancelledPaintMode = editor?.IsPaintMode == true;
            if (cancelledPaintMode)
            {
                editor!.CancelPaintMode();
            }

            if (cancelledGesture || cancelledPaintMode)
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && ViewModel.HasOverlay)
        {
            _ = await TryCloseOverlayAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.ExitTerminalCopyMode())
        {
            FocusActivePanel();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsLayoutDesignerVisible)
        {
            return;
        }

        var commandModifier = OperatingSystem.IsMacOS()
            ? AvaloniaKeyModifiers.Meta
            : AvaloniaKeyModifiers.Control;
        if (IsExactGlobalGesture(e.Key, e.KeyModifiers, Key.K, commandModifier))
        {
            ShowCommandPalette();
            e.Handled = true;
        }
        else if (IsExactGlobalGesture(e.Key, e.KeyModifiers, Key.D1, commandModifier))
        {
            NavigateToLauncher();
            e.Handled = true;
        }
        else if (IsExactGlobalGesture(
            e.Key,
            e.KeyModifiers,
            Key.OemComma,
            commandModifier))
        {
            NavigateToSettings();
            e.Handled = true;
        }
        else if (IsExactGlobalGesture(e.Key, e.KeyModifiers, Key.T, commandModifier))
        {
            await RequestNewTerminalAsync();
            e.Handled = true;
        }
        else if (ViewModel.ActivePanel is TerminalRuntimePanelViewModel { IsCopyMode: true }
            && !IsTerminalCopyGesture(e))
        {
            // Local copy mode leaves mouse selection and scrolling available but
            // prevents ordinary key presses from mutating the live remote shell.
            e.Handled = true;
        }
    }

    private void OnTerminalApplicationKeyPressed(
        object? sender,
        NativeRendererKeyInputEventArgs e)
    {
        if (!ViewModel.IsWorkspaceVisible || ViewModel.HasOverlay)
        {
            _applicationKeys.Reset();
            ClearApplicationKeySequenceHint();
            return;
        }

        SynchronizeApplicationKeymap();
        var resolution = _applicationKeys.Resolve(
            e.Input.Stroke,
            ViewModel.ActiveCommandContexts,
            DateTimeOffset.UtcNow);
        e.Handled = resolution.ShouldHandle;
        if (resolution.Kind != ApplicationKeyResolutionKind.NotHandled)
        {
            // The native shim is waiting for a synchronous consume/pass-through
            // decision. Run any resulting shell command after unwinding its key stack.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                _ = ApplyApplicationKeyResolutionAsync(
                    resolution,
                    sender as TerminalPresentationHost ?? FindActiveTerminalHost()));
        }
    }

    private async Task ApplyApplicationKeyResolutionAsync(
        ApplicationKeyResolution resolution,
        TerminalPresentationHost? replayTarget)
    {
        if (resolution.Kind == ApplicationKeyResolutionKind.Matched
            && resolution.Binding is { } binding)
        {
            ClearApplicationKeySequenceHint();
            await ExecuteCommandAsync(binding);
        }
        else if (resolution.Kind == ApplicationKeyResolutionKind.Pending
            && ViewModel.ActiveApplicationKeymap.Prefix is { } prefix)
        {
            ShowPendingApplicationKeySequenceHint(prefix, replayTarget);
        }
        else if (resolution.Kind == ApplicationKeyResolutionKind.Rejected
            && resolution.ShouldHandle)
        {
            ShowApplicationKeySequenceHint(
                "That key is not bound after the application prefix.",
                TimeSpan.FromSeconds(2));
        }
        else if (resolution.Kind is ApplicationKeyResolutionKind.PassedThrough
            or ApplicationKeyResolutionKind.Expired)
        {
            ClearApplicationKeySequenceHint();
            await ReplayApplicationKeyStrokesAsync(
                replayTarget,
                resolution.ReplayStrokes);
        }
    }

    private void SynchronizeApplicationKeymap()
    {
        var profile = ViewModel.ActiveApplicationKeymap;
        var revision = ViewModel.ActiveApplicationKeymapRevision;
        if (profile.Id == _activeApplicationKeymapId
            && revision == _activeApplicationKeymapRevision)
        {
            return;
        }

        _applicationKeys = new ApplicationKeySequenceResolver(profile);
        _activeApplicationKeymapId = profile.Id;
        _activeApplicationKeymapRevision = revision;
        ClearApplicationKeySequenceHint();
    }

    private void ShowApplicationKeySequenceHint(string message, TimeSpan duration)
    {
        _applicationHintLifetime?.Cancel();
        _applicationHintLifetime?.Dispose();
        _applicationHintLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ViewModel.ShowApplicationKeySequenceHint(message);
        _ = ClearApplicationKeySequenceHintAfterAsync(duration, _applicationHintLifetime.Token);
    }

    private void ShowPendingApplicationKeySequenceHint(
        PrefixConfiguration prefix,
        TerminalPresentationHost? replayTarget)
    {
        _applicationHintLifetime?.Cancel();
        _applicationHintLifetime?.Dispose();
        _applicationHintLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ViewModel.ShowApplicationKeySequenceHint(
            $"{prefix.Stroke} — waiting for a command · {ViewModel.ActiveApplicationKeymapName}");
        _ = ExpireApplicationKeySequenceAsync(
            prefix.Timeout,
            replayTarget,
            _applicationHintLifetime.Token);
    }

    private async Task ClearApplicationKeySequenceHintAfterAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            ViewModel.ClearApplicationKeySequenceHint();
            _applicationKeys.Reset();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExpireApplicationKeySequenceAsync(
        TimeSpan duration,
        TerminalPresentationHost? replayTarget,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            var expiration = _applicationKeys.Expire(
                DateTimeOffset.UtcNow + TimeSpan.FromTicks(1));
            ViewModel.ClearApplicationKeySequenceHint();
            if (expiration.Kind == ApplicationKeyResolutionKind.Expired)
            {
                await ReplayApplicationKeyStrokesAsync(
                    replayTarget,
                    expiration.ReplayStrokes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReplayApplicationKeyStrokesAsync(
        TerminalPresentationHost? replayTarget,
        IReadOnlyList<KeyStroke>? strokes)
    {
        if (strokes is null || strokes.Count == 0)
        {
            return;
        }

        if (replayTarget is null)
        {
            ViewModel.SetError("The application shortcut could not be passed through because no terminal is active.");
            return;
        }

        if (!await replayTarget.ReplayApplicationKeyStrokesAsync(strokes, _lifetime.Token))
        {
            ViewModel.SetError("The application shortcut could not be passed through safely.");
        }
    }

    private void ClearApplicationKeySequenceHint()
    {
        _applicationHintLifetime?.Cancel();
        _applicationHintLifetime?.Dispose();
        _applicationHintLifetime = null;
        ViewModel.ClearApplicationKeySequenceHint();
    }

    private static bool IsTerminalCopyGesture(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.Key == Key.C && (e.KeyModifiers & AvaloniaKeyModifiers.Meta) != 0
        : e.Key == Key.C
            && (e.KeyModifiers & AvaloniaKeyModifiers.Control) != 0
            && (e.KeyModifiers & AvaloniaKeyModifiers.Shift) != 0;

    private void FocusCurrentRoute()
    {
        if (ViewModel.IsWorkspaceVisible)
        {
            FocusActivePanel();
        }
        else if (ViewModel.IsLauncherVisible)
        {
            if (ViewModel.IsLauncherHistoryVisible)
            {
                FocusLauncherWhenReady(static launcher =>
                    launcher.FocusHistorySearch());
            }
            else
            {
                FocusLauncherWhenReady(static launcher =>
                    launcher.FocusHomeNavigation());
            }
        }
        else if (ViewModel.IsSettingsVisible)
        {
            FocusSettingsWhenReady(static settings =>
                settings.FocusBackButton());
        }
    }

    private void FocusControlWhenReady(string controlName) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            this.FindControl<Control>(controlName)?.Focus(NavigationMethod.Tab));

    private void FocusLauncherWhenReady(Action<LauncherView> focus) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<LauncherView>("LauncherRouteView") is { } launcher)
            {
                focus(launcher);
            }
        });

    private void FocusLayoutDesignerNameWhenReady() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.IsLayoutDesignerVisible)
            {
                LayoutDesignerOverlay.FocusNameEditor();
            }
        });

    private void FocusDefinitionEditorWhenReady() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.IsDefinitionEditorVisible)
            {
                this.FindControl<WorkspaceEditorView>("WorkspaceDefinitionEditor")
                    ?.FocusInitialControl();
            }
        });

    private void FocusSelectedLayoutSlot()
    {
        var selected = ViewModel.LayoutDesignerEditor?.SelectedSlot;
        if (selected is null)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            LayoutDesignerOverlay.FocusSlot(selected));
    }


    private async Task CloseWindowAsync()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog().ShowDialog<bool>(this))
        {
            return;
        }

        if (viewModel.KeybindingEditorSession?.IsDirty == true
            && !await new DiscardChangesDialog(
                    "Discard keybinding changes?",
                    "The unsaved shortcuts, prefix, and conflict resolutions will be lost when GhostShell closes.")
                .ShowDialog<bool>(this))
        {
            return;
        }

        if (viewModel.WorkspaceEditor?.RequestCancel()
                == WorkspaceEditorCancelDisposition.ConfirmDiscard
            && !await new DiscardChangesDialog(
                    "Discard workspace changes?",
                    "The unsaved workspace order, tabs, panels, and startup settings will be lost when GhostShell closes.")
                .ShowDialog<bool>(this))
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            if (await RunCloseFlowAsync(viewModel.CloseWindowAsync))
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void RestoreFocusAfterCancelledClose()
    {
        _restoreRouteFocusWhenActivated = true;
        Activate();
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RestoreRouteFocusIfActive,
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_restoreRouteFocusWhenActivated)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                RestoreRouteFocusIfActive,
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void RestoreRouteFocusIfActive()
    {
        if (!_restoreRouteFocusWhenActivated || !IsVisible || !IsActive)
        {
            return;
        }

        _restoreRouteFocusWhenActivated = false;
        FocusCurrentRoute();
    }

    private async Task<bool> RunCloseFlowAsync(
        Func<CloseDecision, CancellationToken, ValueTask<HostResult<CloseScopeResult>>> close)
        => await MainWindowCloseFlow.RunAsync(
            close,
            confirmation => new CloseConfirmationDialog(confirmation).ShowDialog<bool>(this),
            ShowErrorAsync,
            RestoreFocusAfterCancelledClose,
            _lifetime.Token);

    private Task ShowErrorAsync(string message) =>
        new OperationErrorDialog(message).ShowDialog(this);

    private static class EmptyCommandArguments
    {
        public static IReadOnlyDictionary<string, string> Instance { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

internal enum NewTerminalTarget
{
    ExistingRuntimeWorkspace,
    DefaultConnectionWorkspace,
}
