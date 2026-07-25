using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.App.Controls;
using GhostShell.App.Views.Overlays;
using GhostShell.Application;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Views;

internal sealed record AppearanceTextScaleOption(string DisplayName, double? Scale);

public sealed partial class MainWindow : Window
{
    private const double RuntimeTabDragThreshold = 6;
    private static readonly DataFormat<RuntimeTabDragPayload> RuntimeTabDragFormat =
        DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>(
            "app.ghostshell.runtime-tab");

    internal static IReadOnlyList<PlatformProfile> AppearancePlatformProfiles { get; } =
        Enum.GetValues<PlatformProfile>();

    internal static IReadOnlyList<AppearanceTextScaleOption> AppearanceTextScaleOptions { get; } =
    [
        new("Follow host", null),
        new("100%", 1),
        new("125%", 1.25),
        new("150%", 1.5),
        new("175%", 1.75),
        new("200%", 2),
        new("250%", 2.5),
    ];

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
    private bool _changingKeybindingProfile;
    private ThemePreference? _appearanceControlsSource;
    private RuntimeTabDragCandidate? _runtimeTabDragCandidate;
    private Grid? _runtimeTabDropTarget;
    private bool _runtimeTabDragInProgress;

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

    private NewPanelChooserView NewPanelChooserOverlay =>
        this.FindControl<NewPanelChooserView>("NewPanelChooserOverlayView")
        ?? throw new InvalidOperationException(
            "The new panel chooser overlay view is unavailable.");

    private SettingsView SettingsRoute =>
        this.FindControl<SettingsView>("SettingsRouteView")
        ?? throw new InvalidOperationException(
            "The settings route view is unavailable.");

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

    private void FocusNewTerminalButton() =>
        FocusOverlayControl("NewTerminalButton", ViewModel.IsNewItemVisible);

    public void NavigateToLauncher()
    {
        ViewModel.ShowLauncher();
        if (ViewModel.IsLauncherVisible && !ViewModel.HasOverlay)
        {
            FocusLauncherWhenReady(static launcher =>
                launcher.FocusHomeNavigation());
        }
    }

    public void NavigateToSettings(SettingsPage page = SettingsPage.Appearance)
    {
        ViewModel.ShowSettings(page);
        if (ViewModel.IsSettingsVisible && !ViewModel.HasOverlay)
        {
            FocusSettingsWhenReady(static settings => settings.FocusBackButton());
        }
    }

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
            FocusOverlayControlWhenReady("NewLayoutName", () => ViewModel.IsLayoutDesignerVisible);
            return;
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        ViewModel.BeginCreateLayout();
        FocusOverlayControlWhenReady("NewLayoutName", () => ViewModel.IsLayoutDesignerVisible);
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

    public void ToggleAgentPanel()
    {
        if (ViewModel.IsWorkspaceVisible && !ViewModel.HasOverlay)
        {
            ViewModel.ToggleAgentPanel();
        }
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

    private async void OnSendAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is null)
        {
            return;
        }

        try
        {
            await ViewModel.SendAgentPromptAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnAgentQuestionResponseKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter
            || e.KeyModifiers != AvaloniaKeyModifiers.None
            || ViewModel.AgentChat is not { CanSubmitQuestionResponse: true } agentChat)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await agentChat.SubmitQuestionResponseAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.SubmitQuestionResponseAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DeclineQuestionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnEnableAgentCapabilityAskClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.EnableCapabilityAskAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnKeepAgentCapabilityOffClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.KeepCapabilityOffAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnCancelAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.StopAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnCancelAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.CancelActiveActionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnClearAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.ClearAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { CanEnableYolo: true } agentChat)
        {
            return;
        }

        var lifetime = await new AgentYoloConfirmationDialog(
                agentChat.TargetTitle,
                agentChat.ExactTarget,
                agentChat.ConnectionBoundary.Length > 0
                    ? agentChat.ConnectionBoundary
                    : "Not reported",
                agentChat.WorkingDirectory.Length > 0
                    ? agentChat.WorkingDirectory
                    : "Not reported")
            .ShowDialog<TimeSpan?>(this);
        if (lifetime is null)
        {
            return;
        }

        try
        {
            await agentChat.EnableYoloAsync(lifetime.Value, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { CanDisableYolo: true } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DisableYoloAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnApproveAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DecideAsync(approved: true, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDenyAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DecideAsync(approved: false, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.RefreshAuditAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.LoadOlderAuditAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
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

    private void OnSettingsBackClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.HasRuntimeWorkspace)
        {
            ViewModel.ShowWorkspace();
            FocusActivePanel();
        }
        else
        {
            NavigateToLauncher();
        }
    }

    private void OnAppearanceSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Appearance);

    private void OnWorkspaceSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Workspaces);

    private void OnKeybindingSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Keybindings);

    private void OnFilesSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Files);

    private void OnTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Terminal);

    private void OnQuickTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.QuickTerminal);

    private void OnSecretsSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Secrets);

    private void OnDiagnosticsSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Diagnostics);

    private void OnAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Agent);

    private void OnMcpSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Mcp);

    private void OnAboutSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.About);

    private void SetSettingsPage(SettingsPage page)
    {
        ViewModel.SettingsPage = page;
        ViewModel.ShowSettings(page);
        if (page == SettingsPage.Appearance)
        {
            RefreshAppearanceControlsFromStoredProfile();
        }
    }

    private void OnOpenThirdPartyNoticesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var path = ProductDocumentLocator.FindThirdPartyNotices();
        if (path is null)
        {
            ViewModel.SetError(
                "The bundled third-party notices could not be found. Reinstall this build.");
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            if (process is null)
            {
                ViewModel.SetError(
                    "The operating system did not provide an application for the notices file.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ViewModel.SetError(
                "The operating system could not open the bundled third-party notices.");
        }
    }

    internal void RefreshAppearanceControlsFromStoredProfile()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var theme = viewModel.ActiveTheme;
        if (_appearanceControlsSource == theme)
        {
            return;
        }

        SettingsRoute.ApplyAppearance(
            theme,
            ResolveApplicationTextScaleOption(theme.TextScaleOverride));
        _appearanceControlsSource = theme;
    }

    internal static AppearanceTextScaleOption ResolveApplicationTextScaleOption(
        double? textScale)
    {
        var standard = AppearanceTextScaleOptions.FirstOrDefault(option =>
            option.Scale == textScale);
        return standard ?? new(
            textScale!.Value.ToString("0.##%", CultureInfo.InvariantCulture),
            textScale);
    }

    private void OnAccentModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        SettingsRoute.UpdateCustomAccentAvailability();
    }

    private async void OnKeybindingProfileSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        _ = e;
        if (_changingKeybindingProfile
            || sender is not ComboBox
            {
                SelectedItem: KeybindingProfileItemViewModel selected,
            } selector
            || selected.Id == ViewModel.KeybindingEditorSession?.ProfileId)
        {
            return;
        }

        if (ViewModel.KeybindingEditorSession?.IsDirty == true
            && !await new DiscardChangesDialog(
                    "Discard keybinding changes?",
                    "The unsaved shortcuts, prefix, and conflict resolutions will be lost.")
                .ShowDialog<bool>(this))
        {
            _changingKeybindingProfile = true;
            selector.SelectedItem = ViewModel.SelectedKeybindingProfile;
            _changingKeybindingProfile = false;
            return;
        }

        ViewModel.SelectKeybindingProfile(selected);
    }

    private void OnCloneKeybindingPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.CloneSelectedKeybindingProfile();
    }

    private async void OnRecordKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: KeybindingEditorRowItemViewModel row }
            || ViewModel.KeybindingEditorSession is not { } editor)
        {
            return;
        }

        var maximumStrokes = editor.Layer == KeymapLayer.Terminal
            ? 1
            : ShortcutRecorderDialog.DefaultMaximumStrokes;
        var sequence = await new ShortcutRecorderDialog(row.Row.Sequence, maximumStrokes)
            .ShowDialog<KeySequence?>(this);
        if (sequence is not null)
        {
            editor.RecordShortcut(row.Id, sequence.Strokes);
        }
    }

    private void OnUnbindKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: KeybindingEditorRowItemViewModel row })
        {
            ViewModel.KeybindingEditorSession?.Unbind(row.Id);
        }
    }

    private void OnResetKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: KeybindingEditorRowItemViewModel row })
        {
            ViewModel.KeybindingEditorSession?.ResetShortcut(row.Id);
        }
    }

    private async void OnRecordKeybindingPrefixClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.KeybindingEditorSession is not { CanEditPrefix: true } editor)
        {
            return;
        }

        var recorded = await new ShortcutRecorderDialog(initial: null, maximumStrokes: 1)
            .ShowDialog<KeySequence?>(this);
        if (recorded is null)
        {
            return;
        }

        if (recorded.Count != 1)
        {
            ViewModel.SetError("An application prefix must contain exactly one key stroke.");
            return;
        }

        editor.RecordPrefix(recorded[0]);
    }

    private void OnClearKeybindingPrefixClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.KeybindingEditorSession is { CanEditPrefix: true } editor)
        {
            editor.ClearPrefix();
        }
    }

    private void OnKeybindingPrefixOptionsChanged(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { IsKeyboardFocusWithin: true }
            || ViewModel.KeybindingEditorSession is not
            {
                CanEditPrefix: true,
                HasPrefix: true,
            } editor
            || SettingsRoute.CaptureKeybindingPrefixOptions()
                is not { } options)
        {
            return;
        }

        editor.UpdatePrefixOptions(
            options.TimeoutMilliseconds,
            options.Repeatable,
            options.FailureBehavior);
    }

    private void OnResetAllKeybindingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.KeybindingEditorSession?.ResetAll();
    }

    private async void OnSaveKeybindingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveKeybindingEditorAsync(_lifetime.Token);
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

    private void OnToggleAgentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ToggleAgentPanel();
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

    private async void OnAddFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowFileProviderEditorAsync(null);
    }

    private async void OnEditFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileProviderProfileItemViewModel profile })
        {
            await ShowFileProviderEditorAsync(profile.Id);
        }
    }

    private async void OnDeleteFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileProviderProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("file provider", profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(FileProviderProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowFileProviderEditorAsync(FileProviderProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateFileProviderEditor(profileId);
            var request = await new FileProviderProfileEditorDialog(editor)
                .ShowDialog<FileProviderProfileSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveFileProviderProfileAsync(request, _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnAddAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowAiProviderEditorAsync(null);
    }

    private async void OnEditAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: AiProviderProfileItemViewModel profile })
        {
            await ShowAiProviderEditorAsync(profile.Id);
        }
    }

    private async void OnDeleteAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: AiProviderProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("AI provider", profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(AiProviderProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowAiProviderEditorAsync(AiProviderProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateAiProviderEditor(profileId);
            var request = await new AiProviderProfileEditorDialog(editor)
                .ShowDialog<AiProviderProfileSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveAiProviderProfileAsync(request, _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnAddMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowMcpServerEditorAsync(null);
    }

    private async void OnEditMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpServerProfileItemViewModel profile })
        {
            await ShowMcpServerEditorAsync(profile.Id);
        }
    }

    private async void OnTestMcpServerClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control
            {
                DataContext: McpServerProfileItemViewModel profile,
            } testControl)
        {
            await ViewModel.TestMcpServerAsync(
                profile,
                _lifetime.Token);
            if (testControl.IsEnabled)
            {
                _ = testControl.Focus();
            }
        }
    }

    private async void OnDeleteMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: McpServerProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog(
                "MCP server",
                profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(McpServerProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowMcpServerEditorAsync(McpServerProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateMcpServerEditor(profileId);
            var request = await new McpServerProfileEditorDialog(editor)
                .ShowDialog<McpServerProfileSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveMcpServerProfileAsync(
                    request,
                    _lifetime.Token);
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
        if (e.Key != Key.Enter
            || sender is not TextBox
            {
                DataContext: BrowserPresentationHost browser,
            } addressBox)
        {
            return;
        }

        e.Handled = true;
        await RunBrowserOperationAsync(token =>
            browser.NavigateAddressAsync(addressBox.Text, token));
    }

    private async void OnBrowserBackClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: BrowserPresentationHost browser })
        {
            await RunBrowserOperationAsync(browser.GoBackAsync);
        }
    }

    private async void OnBrowserForwardClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: BrowserPresentationHost browser })
        {
            await RunBrowserOperationAsync(browser.GoForwardAsync);
        }
    }

    private async void OnBrowserReloadClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: BrowserPresentationHost browser })
        {
            await RunBrowserOperationAsync(browser.ReloadAsync);
        }
    }

    private async void OnBrowserStopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: BrowserPresentationHost browser })
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

    private async void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = this.FindControl<TextBox>("NewWorkspaceName");
        var result = await ViewModel.CreateWorkspaceAsync(
            input?.Text ?? string.Empty,
            _lifetime.Token);
        if (result.IsSuccess)
        {
            if (input is not null)
            {
                input.Text = string.Empty;
            }

            ViewModel.CloseOverlay();
        }
    }

    private async void OnCreateScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = this.FindControl<TextBox>("NewScreenName");
        try
        {
            var editor = ViewModel.CreateNewSavedScreenEditor(input?.Text ?? string.Empty);
            var saved = await new SavedScreenEditorDialog(
                    editor,
                    ViewModel.SaveSavedScreenAsync)
                .ShowDialog<bool>(this);
            if (!saved)
            {
                return;
            }

            if (input is not null)
            {
                input.Text = string.Empty;
            }

            ViewModel.CloseOverlay();
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
        FocusOverlayControlWhenReady("NewLayoutName", () => ViewModel.IsLayoutDesignerVisible);
    }

    private void OnLayoutGridSizeChanged(
        object? sender,
        NumericUpDownValueChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var editor = ViewModel.LayoutDesignerEditor;
        var rows = this.FindControl<NumericUpDown>("LayoutRowsPicker")?.Value;
        var columns = this.FindControl<NumericUpDown>("LayoutColumnsPicker")?.Value;
        if (editor is null || rows is null || columns is null)
        {
            return;
        }

        _ = editor.ResizeGrid((int)rows.Value, (int)columns.Value);
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
            _ = this.FindControl<ItemsControl>("LayoutDesignerGrid")?.Focus();
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

    private async void OnSaveAppearanceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            var selection = SettingsRoute.CaptureAppearance();
            var result = await ViewModel.SaveThemeAsync(
                selection.Appearance,
                selection.PlatformProfile,
                selection.Accent,
                selection.TextScale,
                _lifetime.Token);
            if (result.IsSuccess)
            {
                _appearanceControlsSource = null;
                RefreshAppearanceControlsFromStoredProfile();
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or InvalidOperationException)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnSaveTerminalProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveTerminalProfileAsync(_lifetime.Token);
    }

    private async void OnSaveQuickTerminalSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveQuickTerminalSettingsAsync(_lifetime.Token);
    }

    private async void OnCreateConnectionSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureConnectionSecretForm();
        if (input.Connection is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose a connection and credential kind.");
            return;
        }

        var created = await ViewModel.CreateConnectionSecretAsync(
            input.Connection.Id,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearConnectionSecretValue();
        if (created)
        {
            SettingsRoute.ClearConnectionSecretLabel();
        }
    }

    private async void OnCreateFileProviderSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureFileProviderSecretForm();
        if (input.Profile is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose a file provider and credential kind.");
            return;
        }

        var created = await ViewModel.CreateFileProviderSecretAsync(
            input.Profile.Id,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearFileProviderSecretValue();
        if (created)
        {
            SettingsRoute.ClearFileProviderSecretLabel();
        }
    }

    private async void OnCreateAiProviderSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureAiProviderSecretForm();
        if (input.Profile is null)
        {
            ViewModel.SetError("Choose an AI-provider profile.");
            return;
        }

        var created = await ViewModel.CreateAiProviderSecretAsync(
            input.Profile.Id,
            input.Label,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearAiProviderSecretValue();
        if (created)
        {
            SettingsRoute.ClearAiProviderSecretLabel();
        }
    }

    private async void OnCreateMcpServerSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureMcpServerSecretForm();
        if (input.Target is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose an MCP environment binding and credential kind.");
            return;
        }

        var created = await ViewModel.CreateMcpServerSecretAsync(
            input.Target,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearMcpServerSecretValue();
        if (created)
        {
            SettingsRoute.ClearMcpServerSecretLabel();
        }
    }

    private async void OnDeleteSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: SecretMetadataViewModel secret })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("credential", secret.Label)
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            _ = await ViewModel.DeleteSecretAsync(secret, _lifetime.Token);
        }
    }

    private async void OnEditSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: SecretMetadataViewModel secret })
        {
            return;
        }

        var request = await new SecretEditorDialog(new SecretEditorViewModel(secret))
            .ShowDialog<SecretEditRequest?>(this);
        if (request is null)
        {
            return;
        }

        using var replacement = request.Replacement;
        if (request.Action == SecretEditAction.Relabel)
        {
            _ = await ViewModel.RelabelSecretAsync(
                secret,
                request.Label,
                _lifetime.Token);
        }
        else if (replacement is not null)
        {
            _ = await ViewModel.ReplaceSecretAsync(
                secret,
                replacement,
                _lifetime.Token);
        }
    }

    private async void OnCancelFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileTransferItemViewModel transfer })
        {
            _ = await ViewModel.CancelFileTransferAsync(transfer.Id, _lifetime.Token);
        }
    }

    private async void OnRetryFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileTransferItemViewModel transfer })
        {
            _ = await ViewModel.RetryFileTransferAsync(transfer.Id, _lifetime.Token);
        }
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
            var cancelledGesture = this.FindControl<ItemsControl>("LayoutDesignerGrid")
                ?.GetVisualDescendants()
                .OfType<LayoutDesignerPreviewPanel>()
                .FirstOrDefault()
                ?.CancelPointerGesture() == true;
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

    private void FocusSettingsWhenReady(Action<SettingsView> focus) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            focus(SettingsRoute));

    private void FocusOverlayControl(string controlName, bool isOverlayVisible)
    {
        if (isOverlayVisible)
        {
            this.FindControl<Control>(controlName)?.Focus(NavigationMethod.Tab);
        }
    }

    private void FocusOverlayControlWhenReady(string controlName, Func<bool> isOverlayVisible) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            FocusOverlayControl(controlName, isOverlayVisible()));

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
            this.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => ReferenceEquals(button.DataContext, selected))
                ?.Focus(NavigationMethod.Directional));
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
