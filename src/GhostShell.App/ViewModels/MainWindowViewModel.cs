using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FluentIcons.Common;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Core;
using GhostShell.Docker;

namespace GhostShell.App.ViewModels;

public enum AgentRunScopeKind
{
    ActivePanel,
    CurrentTab,
    Workspace,
    SelectedPanels,
}

public sealed record AgentRunScopeOption(
    AgentRunScopeKind Kind,
    string Label);

public sealed record ManagedRemoteSessionViewModel(
    TerminalMultiplexerLease Lease,
    string ConnectionName)
{
    public string SessionName => Lease.Session.SessionName;

    public string Status => Lease.State == TerminalMultiplexerLeaseState.Active
        ? "Detached or active"
        : "Cleanup pending";

    public bool IsCleanupPending =>
        Lease.State == TerminalMultiplexerLeaseState.TerminationPending;
}

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private enum McpServerTestPresentationState
    {
        Testing,
        Succeeded,
        Failed,
    }

    private sealed record McpServerTestPresentation(
        long Revision,
        McpServerTestPresentationState State,
        string Status,
        string Detail);

    private enum RuntimeGraphStaleProposalHandling
    {
        RefreshAndRetry,
        Reject,
    }

    private readonly record struct RuntimeMutationNavigationSnapshot(
        ShellRoute Route,
        ShellOverlay Overlay,
        long OverlayRevision);

    private const int WorkspaceMutationAttemptCount = 2;
    private static readonly TimeSpan WorkspaceGraphReceiptReconciliationTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyList<AgentRunScopeOption> AgentRunScopeOptionsValue =
        Array.AsReadOnly<AgentRunScopeOption>(
        [
            new(AgentRunScopeKind.ActivePanel, "Active panel"),
            new(AgentRunScopeKind.CurrentTab, "Current tab"),
            new(AgentRunScopeKind.Workspace, "Workspace"),
            new(AgentRunScopeKind.SelectedPanels, "Selected terminals"),
        ]);

    private readonly IDefinitionCatalog _catalog;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly ISecretVault _secretVault;
    private readonly IFilePanelClient _filePanelClient;
    private readonly IFileTransferQueueClient _fileTransferQueue;

    /// <summary>
    /// What has been copied or cut, for the whole window: copying in one panel
    /// and pasting into another is the whole point of it, so it cannot belong
    /// to either of them.
    /// </summary>
    public FileTransferClipboard FileTransferClipboard { get; } = new();
    private readonly IBrowserRendererViewFactory? _browserRendererViewFactory;
    private readonly IDatabasePanelClient? _databasePanelClient;
    private readonly IDockerEngineClient? _dockerEngineClient;
    private readonly ISqlLanguageService? _sqlLanguageService;
    private readonly IImagePreviewDecoder? _imagePreviewDecoder;
    private readonly IPdfPreviewRenderer? _pdfPreviewRenderer;
    private readonly IArchiveTableOfContents? _archiveTableOfContents;
    private readonly IInMemoryDatabaseRegistry? _inMemoryDatabaseRegistry;
    private readonly IFilePreviewPreferences _filePreviewPreferences;
    private readonly TerminalStartupCommandDispatcher _startupCommandDispatcher;
    private readonly IFileProviderProfileRuntime? _fileProviderRuntime;
    private readonly IAiProviderProfileRuntime? _aiProviderRuntime;
    private readonly IMcpServerDiagnostics? _mcpServerDiagnostics;
    private readonly IMcpCredentialSessionInvalidator?
        _mcpCredentialSessionInvalidator;
    private readonly IConnectionSecurityRuntime? _connectionSecurityRuntime;
    private readonly RuntimeRecoveryWriter? _runtimeRecoveryWriter;
    private readonly SessionRestoreCoordinator? _sessionRestoreCoordinator;
    private readonly TerminalMultiplexerCoordinator? _terminalMultiplexerCoordinator;
    private readonly RecentSessionHistory? _recentSessionHistory;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _historyLifetime = new();
    private readonly CancellationTokenSource _runtimeGraphLifetime = new();
    private readonly SemaphoreSlim _runtimeGraphGate = new(1, 1);
    private readonly object _historyGate = new();
    private readonly object _mcpServerTestGate = new();
    private readonly object _shutdownGate = new();
    private readonly Dictionary<PanelInstanceId, SessionId> _recentSessionIds = [];
    private readonly Dictionary<WorkspaceInstanceId, TerminalMultiplexingMode>
        _workspaceTerminalMultiplexingModes = [];
    private readonly List<Task> _runtimeGraphWatchTasks = [];
    private readonly HashSet<RuntimeTabViewModel> _agentSelectionTrackedTabs = [];
    private readonly HashSet<TerminalRuntimePanelViewModel>
        _agentSelectionTrackedTerminals = [];
    private readonly HashSet<FilePanelTransferId> _refreshedFileTransfers = [];
    private readonly Dictionary<
        McpServerProfileId,
        McpServerTestPresentation> _mcpServerTests = [];
    private Task _historyOperations = Task.CompletedTask;
    private Task? _shutdownTask;
    private RecentSessionStoreError? _historyDrainError;

    /// <summary>
    /// Sessions this process has already ended. A session ends once; the
    /// store enforces that, and this keeps the app from asking twice.
    /// </summary>
    private readonly HashSet<SessionId> _completedSessionIds = [];
    private CancellationTokenSource? _runtimeGraphWatchCancellation;
    private CancellationTokenSource? _workspaceAutoSaveDebounce;
    private RuntimeHistorySource? _runtimeHistorySource;
    private ShellRoute _route = ShellRoute.Workspace;
    private SettingsPage _settingsPage = SettingsPage.Appearance;
    private ShellOverlay _overlay;
    private long _overlayRevision;
    private RuntimeWorkspaceViewModel? _runtimeWorkspace;
    private string? _operationError;
    private string _tabReorderStatus = string.Empty;
    private string _launcherSearchQuery = string.Empty;
    private string _historySearchQuery = string.Empty;
    private bool _isAgentPanelVisible;
    private bool _isAgentPanelDocked;
    private DefinitionKey? _editingDefinition;
    private long? _editingRevision;
    private string _editorName = string.Empty;
    private string _editorDescription = string.Empty;
    private string _secretVaultStatus = "Checking the operating-system vault…";
    private string _recentSessionStatus =
        "Sessions you open will appear here without storing terminal content or commands.";
    private bool _hasRecentSessionFailure;
    private bool _hasUnreadableRecentSessionHistory;
    private bool _isHistoryLoading;
    private bool _isHistoryMutating;
    private bool _isHistoryExporting;
    private bool _historyOperationsSealed;
    private string _definitionBundleStatus =
        "Exported bundles contain durable definitions only; credential values and runtime terminal data are excluded.";
    private string? _applicationKeySequenceHint;
    private LayoutDesignerViewModel? _layoutDesignerEditor;
    private WorkspaceEditorViewModel? _workspaceEditor;
    private KeybindingProfileItemViewModel? _selectedKeybindingProfile;
    private KeybindingEditorSessionViewModel? _keybindingEditorSession;
    private TerminalProfileEditorViewModel? _terminalSettingsEditor;
    private QuickTerminalSettingsEditorViewModel? _quickTerminalSettingsEditor;
    private LauncherSearchResultViewModel? _selectedLauncherSearchResult;
    private RecentSessionHistoryItemViewModel? _selectedHistorySession;
    private HistoryExportScope _selectedHistoryExportScope;
    private StoredRecentSessionRetentionPolicy? _storedHistoryRetention;
    private bool _restoreSessionsOnStart = true;
    private bool _sessionRestorePreferenceLoaded;
    private bool _sessionRestorePreferenceSaving;
    private TerminalMultiplexingMode _terminalMultiplexingMode;
    private bool _terminalMultiplexingPreferenceLoaded;
    private bool _terminalMultiplexingPreferenceSaving;
    private HistoryRetentionOption? _selectedHistoryRetentionOption;
    private bool _isApplyingStoredHistoryRetention;
    private bool _hasPendingHistoryRetentionChange;
    private string _historyExportStatus =
        "History exports contain definition metadata only; terminal commands and content are excluded.";
    private string _historyRetentionStatus = "Loading local history privacy settings…";
    private AgentRunScopeOption _selectedAgentRunScope =
        AgentRunScopeOptionsValue[0];
    private bool _agentTerminalSelectionStale;
    private bool _hasAgentTerminalSelectionError;
    private string _agentTerminalSelectionStatus =
        $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} live terminals from this workspace.";
    private volatile bool _shutdownStarted;
    private bool _presentationTeardownCompleted;
    private bool _disposed;

    public MainWindowViewModel(
        ISessionHostClient sessionClient,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        ISecretVault secretVault,
        IFilePanelClient filePanelClient,
        IFileTransferQueueClient fileTransferQueue,
        TerminalStartupCommandDispatcher startupCommandDispatcher,
        IFileProviderProfileRuntime? fileProviderRuntime = null,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null,
        RuntimeRecoveryWriter? runtimeRecoveryWriter = null,
        RecentSessionHistory? recentSessionHistory = null,
        TimeProvider? timeProvider = null,
        IUiThreadDispatcher? uiThreadDispatcher = null,
        OnboardingViewModel? onboarding = null,
        IProductComponentCatalog? productComponentCatalog = null,
        IAiProviderProfileRuntime? aiProviderRuntime = null,
        IGovernedAgentRuntime? agentChatRuntime = null,
        IAgentApprovalPrincipal? agentApprovalPrincipal = null,
        IBrowserRendererViewFactory? browserRendererViewFactory = null,
        IDatabasePanelClient? databasePanelClient = null,
        IImagePreviewDecoder? imagePreviewDecoder = null,
        IPdfPreviewRenderer? pdfPreviewRenderer = null,
        IArchiveTableOfContents? archiveTableOfContents = null,
        IInMemoryDatabaseRegistry? inMemoryDatabaseRegistry = null,
        IFilePreviewPreferences? filePreviewPreferences = null,
        IPreviewCacheControl? previewCacheControl = null,
        IApplicationEncryption? applicationEncryption = null,
        IStartupProtection? startupProtection = null,
        IBiometricAuthenticator? biometricAuthenticator = null,
        IAgentRunAuditReader? agentRunAuditReader = null,
        IMcpServerDiagnostics? mcpServerDiagnostics = null,
        IMcpCredentialSessionInvalidator?
            mcpCredentialSessionInvalidator = null,
        SessionRestoreCoordinator? sessionRestoreCoordinator = null,
        ISqlLanguageService? sqlLanguageService = null,
        IDockerEngineClient? dockerEngineClient = null,
        TerminalMultiplexerCoordinator? terminalMultiplexerCoordinator = null)
    {
        SessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        OpenWorkspaces = new(_openWorkspaces);
        Notifications = new ShellNotificationCenter(
            () => RuntimeWorkspace,
            () => IsWindowFocused,
            RefreshWorkspaceRuntimeFlags);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        SavedScreenDeleteUndo = new SavedScreenDeleteUndoViewModel(_catalog);
        _connectionRuntime = connectionRuntime ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _filePanelClient = filePanelClient ?? throw new ArgumentNullException(nameof(filePanelClient));
        _fileTransferQueue = fileTransferQueue
            ?? throw new ArgumentNullException(nameof(fileTransferQueue));
        _browserRendererViewFactory = browserRendererViewFactory;
        _databasePanelClient = databasePanelClient;
        _dockerEngineClient = dockerEngineClient;
        _sqlLanguageService = sqlLanguageService;
        _imagePreviewDecoder = imagePreviewDecoder;
        _pdfPreviewRenderer = pdfPreviewRenderer;
        _archiveTableOfContents = archiveTableOfContents;
        _inMemoryDatabaseRegistry = inMemoryDatabaseRegistry;
        _filePreviewPreferences = filePreviewPreferences ?? new InMemoryFilePreviewPreferences();
        FilePreviewSettingsEditor = new FilePreviewSettingsEditorViewModel(
            _filePreviewPreferences,
            previewCacheControl);
        ApplicationSecurityEditor = new ApplicationSecurityEditorViewModel(
            applicationEncryption,
            startupProtection,
            biometricAuthenticator);
        _startupCommandDispatcher = startupCommandDispatcher
            ?? throw new ArgumentNullException(nameof(startupCommandDispatcher));
        _fileProviderRuntime = fileProviderRuntime ?? filePanelClient as IFileProviderProfileRuntime;
        _aiProviderRuntime = aiProviderRuntime;
        _mcpServerDiagnostics = mcpServerDiagnostics;
        _mcpCredentialSessionInvalidator =
            mcpCredentialSessionInvalidator;
        _connectionSecurityRuntime = connectionSecurityRuntime;
        _runtimeRecoveryWriter = runtimeRecoveryWriter;
        _sessionRestoreCoordinator = sessionRestoreCoordinator;
        _terminalMultiplexerCoordinator = terminalMultiplexerCoordinator;
        if (_terminalMultiplexerCoordinator is not null)
        {
            _terminalMultiplexerCoordinator.LeasesChanged +=
                OnTerminalMultiplexerLeasesChanged;
        }
        _recentSessionHistory = recentSessionHistory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiThreadDispatcher = uiThreadDispatcher ?? AvaloniaUiThreadDispatcher.Instance;
        ClientId = agentApprovalPrincipal is null
            ? ClientId.New()
            : RequireDesktopClientId(agentApprovalPrincipal);
        WindowId = WindowInstanceId.New();
        AgentChat = agentChatRuntime is not null && _aiProviderRuntime is not null
            ? new AgentChatViewModel(
                agentChatRuntime,
                _aiProviderRuntime,
                _uiThreadDispatcher,
                agentRunAuditReader)
            : null;
        Onboarding = onboarding;
        ProductComponents = productComponentCatalog?.Components ?? [];
        _catalog.Changed += OnCatalogChanged;
        _fileTransferQueue.TransfersChanged += OnFileTransfersChanged;
        if (_fileProviderRuntime is not null)
        {
            _fileProviderRuntime.ProfilesChanged += OnFileProviderProfilesChanged;
        }
        if (_aiProviderRuntime is not null)
        {
            _aiProviderRuntime.ProfilesChanged += OnAiProviderProfilesChanged;
        }
        if (_runtimeRecoveryWriter is not null)
        {
            _runtimeRecoveryWriter.WriteFailed += OnRuntimeRecoveryWriteFailed;
        }
        RefreshCatalog(_catalog.Snapshot);
        RefreshFileTransfers();
        _ = RefreshSecretsAsync(CancellationToken.None);
        Onboarding?.Start();
        if (_recentSessionHistory is not null)
        {
            IsHistoryLoading = true;
            _ = QueueHistoryOperation(async token =>
            {
                try
                {
                    await RefreshRecentSessionsCoreAsync(token);
                }
                finally
                {
                    IsHistoryLoading = false;
                }
            });
        }
    }

    public ISessionHostClient SessionClient { get; }

    /// <summary>
    /// The preview settings the Files &amp; transfers page edits. Always
    /// present: without stored preferences it edits in-memory ones, so the
    /// page behaves the same everywhere it renders.
    /// </summary>
    public FilePreviewSettingsEditorViewModel FilePreviewSettingsEditor { get; }

    /// <summary>
    /// The application-security controls on the Security &amp; secrets page.
    /// Always present; without an encryption service it reports itself
    /// unavailable rather than not rendering.
    /// </summary>
    public ApplicationSecurityEditorViewModel ApplicationSecurityEditor { get; }

    public ObservableCollection<ManagedRemoteSessionViewModel> ManagedRemoteSessions { get; } = [];

    public OnboardingViewModel? Onboarding { get; }

    public AgentChatViewModel? AgentChat { get; }

    public SavedScreenDeleteUndoViewModel SavedScreenDeleteUndo { get; }

    public ClientId ClientId { get; }

    public WindowInstanceId WindowId { get; }

    public IReadOnlyList<AgentRunScopeOption> AgentRunScopeOptions =>
        AgentRunScopeOptionsValue;

    public AgentRunScopeOption SelectedAgentRunScope
    {
        get => _selectedAgentRunScope;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AgentRunScopeOptionsValue.Contains(value))
            {
                throw new ArgumentException(
                    "The selected agent scope is not available.",
                    nameof(value));
            }

            if (AgentChat is { CanChangeProvider: false })
            {
                return;
            }

            if (SetProperty(ref _selectedAgentRunScope, value))
            {
                OnPropertyChanged(nameof(IsAgentSelectedPanelsScope));
                UpdateAgentTerminalSelectionStatus();
            }
        }
    }

    public ObservableCollection<AgentTerminalSelectionItemViewModel> AgentTerminalSelectionOptions
    { get; } = [];

    public bool IsAgentSelectedPanelsScope =>
        SelectedAgentRunScope.Kind == AgentRunScopeKind.SelectedPanels;

    public bool HasAgentTerminalSelectionOptions =>
        AgentTerminalSelectionOptions.Count > 0;

    public int AgentSelectedTerminalCount =>
        AgentTerminalSelectionOptions.Count(option => option.IsSelected);

    public string AgentTerminalSelectionSummary =>
        $"{AgentSelectedTerminalCount} selected";

    public string AgentTerminalSelectionStatus
    {
        get => _agentTerminalSelectionStatus;
        private set => SetProperty(ref _agentTerminalSelectionStatus, value);
    }

    public bool HasAgentTerminalSelectionError
    {
        get => _hasAgentTerminalSelectionError;
        private set => SetProperty(ref _hasAgentTerminalSelectionError, value);
    }

    public ObservableCollection<LauncherWorkspaceViewModel> Workspaces { get; } = [];

    public ObservableCollection<LauncherConnectionViewModel> Connections { get; } = [];

    /// <summary>
    /// Saved file-transfer providers, presented as connection cards so the
    /// launcher manages every connection family in one place.
    /// </summary>
    public ObservableCollection<LauncherConnectionViewModel> FileConnections { get; } = [];

    /// <summary>Saved database connections, presented as connection cards.</summary>
    public ObservableCollection<LauncherConnectionViewModel> DatabaseConnections { get; } = [];

    public IReadOnlyList<SavedConnectionShortcutViewModel> SavedConnectionShortcuts =>
        BuildSavedConnectionShortcuts();

    /// <summary>
    /// Counted here rather than as <c>SavedConnectionShortcuts.Count</c> in the
    /// view: the list is an array behind an interface, whose runtime type has
    /// no public Count for a binding to reflect over, so the pill would render
    /// empty and say nothing about it.
    /// </summary>
    public int SavedConnectionShortcutCount => SavedConnectionShortcuts.Count;

    public IEnumerable<PanelConnectionOptionViewModel> PanelConnectionOptions =>
        Connections.Select(connection => new PanelConnectionOptionViewModel(
            new PanelConnectionOptionViewModel.Target.Connection(connection.Id),
            connection.Name,
            connection.Kind,
            connection.Detail,
            connection.CanOpen));

    public IEnumerable<PanelConnectionOptionViewModel> BrowserConnectionOptions =>
        Connections
            .Where(connection => FindConnection(connection.Id)?.Endpoint is
                ConnectionEndpoint.Local or ConnectionEndpoint.Ssh)
            .Select(connection => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.Connection(connection.Id),
                connection.Name,
                connection.Kind,
                connection.Detail,
                CanOpen: true));

    public IReadOnlyList<PanelConnectionOptionViewModel> FileConnectionOptions =>
        BuildFileConnectionOptions();

    /// <summary>
    /// What the database panel's connection pill offers: saved database
    /// connections — not tunnels; a profile carries its own route.
    /// </summary>
    public IEnumerable<PanelConnectionOptionViewModel> DatabasePanelConnectionOptions =>
        _catalog.Snapshot.DatabaseConnections.Select(item =>
        {
            var profile = item.Value;
            var driver = _databasePanelClient?.Drivers
                .FirstOrDefault(descriptor => descriptor.Id == profile.DriverId);
            return new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.Database(profile.Id),
                profile.Name,
                driver?.DisplayName ?? profile.DriverId,
                DatabaseConnectionDetailText(profile),
                CanOpen: _databasePanelClient is not null);
        });

    public ObservableCollection<LauncherScreenViewModel> Screens { get; } = [];

    public ObservableCollection<LayoutCardViewModel> Layouts { get; } = [];

    public ObservableCollection<KeybindingRowViewModel> Keybindings { get; } = [];

    public ObservableCollection<KeybindingProfileItemViewModel> KeybindingProfiles { get; } = [];

    public ObservableCollection<LauncherSearchResultViewModel> LauncherSearchResults { get; } = [];

    public ObservableCollection<SecretMetadataViewModel> Secrets { get; } = [];

    public bool HasNoSecrets => Secrets.Count == 0;

    /// <summary>
    /// The browser-view factory, for surfaces that host a page of their own — the
    /// file preview of an HTML document uses the same engine the browser panel
    /// does rather than a second one.
    /// </summary>
    public IBrowserRendererViewFactory? BrowserRendererViewFactory =>
        _browserRendererViewFactory;

    public ObservableCollection<FileTransferItemViewModel> FileTransfers { get; } = [];

    public ObservableCollection<FileProviderProfileItemViewModel> FileProviderDefinitions { get; } = [];

    public ObservableCollection<AiProviderProfileItemViewModel> AiProviderDefinitions { get; } = [];

    public ObservableCollection<McpServerProfileItemViewModel> McpServerDefinitions { get; } = [];

    public ObservableCollection<McpEnvironmentSecretTargetViewModel>
        McpEnvironmentSecretTargets
    { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> RecentSessions { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> HistorySessions { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> FilteredHistorySessions { get; } = [];

    public IReadOnlyList<HistoryExportScope> HistoryExportScopes { get; } =
        Enum.GetValues<HistoryExportScope>();

    public ObservableCollection<HistoryRetentionOption> HistoryRetentionOptions { get; } =
    [
        new(
            "Off",
            "Do not retain session metadata. Existing history is removed.",
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(30))),
        new(
            "Private · 20 / 7 days",
            "Keep at most 20 records for up to 7 days.",
            new RecentSessionRetentionPolicy(20, TimeSpan.FromDays(7))),
        new(
            "Standard · 100 / 30 days",
            "Keep at most 100 records for up to 30 days.",
            RecentSessionRetentionPolicy.Default),
        new(
            "Extended · 500 / 90 days",
            "Keep at most 500 records for up to 90 days.",
            new RecentSessionRetentionPolicy(500, TimeSpan.FromDays(90))),
        new(
            "Maximum · 1,000 / 365 days",
            "Keep at most 1,000 records for up to 365 days.",
            new RecentSessionRetentionPolicy(1_000, TimeSpan.FromDays(365))),
    ];

    public LayoutDesignerViewModel? LayoutDesignerEditor
    {
        get => _layoutDesignerEditor;
        private set => SetProperty(ref _layoutDesignerEditor, value);
    }

    public WorkspaceEditorViewModel? WorkspaceEditor
    {
        get => _workspaceEditor;
        private set
        {
            if (ReferenceEquals(_workspaceEditor, value))
            {
                return;
            }

            var previous = _workspaceEditor;
            if (SetProperty(ref _workspaceEditor, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasWorkspaceEditor));
            }
        }
    }

    public bool HasWorkspaceEditor => WorkspaceEditor is not null;

    public KeybindingProfileItemViewModel? SelectedKeybindingProfile
    {
        get => _selectedKeybindingProfile;
        private set
        {
            if (SetProperty(ref _selectedKeybindingProfile, value))
            {
                OnPropertyChanged(nameof(CanCloneSelectedKeybindingProfile));
            }
        }
    }

    public KeybindingEditorSessionViewModel? KeybindingEditorSession
    {
        get => _keybindingEditorSession;
        private set
        {
            if (ReferenceEquals(_keybindingEditorSession, value))
            {
                return;
            }

            var previous = _keybindingEditorSession;
            if (SetProperty(ref _keybindingEditorSession, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasKeybindingEditor));
            }
        }
    }

    public bool HasKeybindingEditor => KeybindingEditorSession is not null;

    public bool CanCloneSelectedKeybindingProfile => SelectedKeybindingProfile?.IsBuiltIn == true;

    public string ApplicationVersion =>
        $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    public string PlatformDescription =>
        $"{PlatformName()} · {RuntimeInformation.ProcessArchitecture}";

    public string AgentRuntimeDescription => AgentChat is null
        ? "Unavailable · provider runtime not composed"
        : AgentChat.RendererModeDescription;

    public string UpdateStatus =>
        "Not configured for this build";

    public IReadOnlyList<ProductComponentViewModel> ProductComponents { get; }

    /// <summary>
    /// A build composed without a component catalog would otherwise leave the
    /// About page promising an inventory and then showing nothing at all.
    /// </summary>
    public bool HasNoProductComponents => ProductComponents.Count == 0;

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasNoWorkspaces => !HasWorkspaces;

    /// <summary>
    /// Home is a summary, so it shows a bounded preview and sends the rest to the
    /// dedicated page. Without the cap a profile with a hundred connections would
    /// push every other section off the page.
    /// </summary>
    public ObservableCollection<LauncherConnectionViewModel> ConnectionsPreview { get; } = [];

    public ObservableCollection<LauncherScreenViewModel> ScreensPreview { get; } = [];

    private const int HomePreviewConnectionCount = 8;

    private const int HomePreviewScreenCount = 4;

    public bool HasMoreConnectionsThanPreview => TotalConnectionCount > ConnectionsPreview.Count;

    public bool HasMoreScreensThanPreview => Screens.Count > ScreensPreview.Count;

    public bool HasConnections => TotalConnectionCount > 0;

    public bool HasNoConnections => !HasConnections;

    public bool HasTerminalConnections => Connections.Count > 0;

    public bool HasFileConnections => FileConnections.Count > 0;

    public bool HasDatabaseConnections => DatabaseConnections.Count > 0;

    public int TotalConnectionCount =>
        Connections.Count + FileConnections.Count + DatabaseConnections.Count;

    public bool HasScreens => Screens.Count > 0;

    public bool HasNoScreens => !HasScreens;

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool HasNoRecentSessions => !HasRecentSessions && !IsHistoryLoading;

    public bool HasHistorySessions => HistorySessions.Count > 0;

    public bool HasNoHistorySessions => !HasHistorySessions;

    public bool HasFilteredHistorySessions => FilteredHistorySessions.Count > 0;

    public bool HasNoFilteredHistorySessions =>
        !HasFilteredHistorySessions && !IsHistoryLoading;

    public bool HasRecentSessionFailure
    {
        get => _hasRecentSessionFailure;
        private set
        {
            if (SetProperty(ref _hasRecentSessionFailure, value))
            {
                NotifyHistoryActionStateChanged();
            }
        }
    }

    public bool CanResetRecentSessionHistory =>
        _recentSessionHistory is not null
        && HasUnreadableRecentSessionHistory
        && !IsHistoryLoading
        && !IsHistoryMutating;

    public bool HasUnreadableRecentSessionHistory
    {
        get => _hasUnreadableRecentSessionHistory;
        private set
        {
            if (SetProperty(ref _hasUnreadableRecentSessionHistory, value))
            {
                OnPropertyChanged(nameof(CanResetRecentSessionHistory));
            }
        }
    }

    public bool IsHistoryLoading
    {
        get => _isHistoryLoading;
        private set
        {
            if (SetProperty(ref _isHistoryLoading, value))
            {
                NotifyHistoryActionStateChanged();
            }
        }
    }

    public bool IsHistoryMutating
    {
        get => _isHistoryMutating;
        private set
        {
            if (SetProperty(ref _isHistoryMutating, value))
            {
                NotifyHistoryActionStateChanged();
            }
        }
    }

    public bool IsHistoryExporting
    {
        get => _isHistoryExporting;
        private set
        {
            if (SetProperty(ref _isHistoryExporting, value))
            {
                NotifyHistoryActionStateChanged();
            }
        }
    }

    public bool CanRetryRecentSessionHistory =>
        HasRecentSessionFailure && !IsHistoryLoading && !IsHistoryMutating;

    public bool CanClearRecentSessionHistory =>
        HasHistorySessions && !IsHistoryLoading && !IsHistoryMutating;

    public bool CanExportAllHistory =>
        HasHistorySessions
        && !IsHistoryLoading
        && !IsHistoryMutating
        && !IsHistoryExporting;

    public bool CanExportFilteredHistory =>
        HasFilteredHistorySessions
        && !IsHistoryLoading
        && !IsHistoryMutating
        && !IsHistoryExporting;

    public string HistoryResultCount => string.IsNullOrWhiteSpace(HistorySearchQuery)
        ? $"{FilteredHistorySessions.Count} retained"
        : $"{FilteredHistorySessions.Count} matched";

    public string HistorySearchEmptyState => HasHistorySessions
        ? $"No retained sessions match ‘{HistorySearchQuery.Trim()}’."
        : RecentSessionStatus;

    public bool HasLauncherSearchResults => LauncherSearchResults.Count > 0;

    public bool HasNoLauncherSearchResults => !HasLauncherSearchResults;

    public string LauncherSearchEmptyState => string.IsNullOrWhiteSpace(LauncherSearchQuery)
        ? "No commands or saved launch targets are available."
        : $"No commands or launch targets match ‘{LauncherSearchQuery.Trim()}’.";

    public string RecentSessionStatus
    {
        get => _recentSessionStatus;
        private set => SetProperty(ref _recentSessionStatus, value);
    }

    public string HistoryExportStatus
    {
        get => _historyExportStatus;
        private set => SetProperty(ref _historyExportStatus, value);
    }

    public string HistoryRetentionStatus
    {
        get => _historyRetentionStatus;
        private set => SetProperty(ref _historyRetentionStatus, value);
    }

    public bool CanManageHistoryRetention =>
        _recentSessionHistory?.SupportsRetentionSettings == true
        && _storedHistoryRetention is not null
        && !IsHistoryLoading
        && !IsHistoryMutating;

    public HistoryRetentionOption? SelectedHistoryRetentionOption
    {
        get => _selectedHistoryRetentionOption;
        set
        {
            if (SetProperty(ref _selectedHistoryRetentionOption, value))
            {
                if (!_isApplyingStoredHistoryRetention)
                {
                    HasPendingHistoryRetentionChange = _storedHistoryRetention is { } stored
                        && value?.Policy != stored.Policy;
                }

                OnPropertyChanged(nameof(RequiresHistoryRetentionConfirmation));
            }
        }
    }

    public bool HasPendingHistoryRetentionChange
    {
        get => _hasPendingHistoryRetentionChange;
        private set
        {
            if (SetProperty(ref _hasPendingHistoryRetentionChange, value))
            {
                OnPropertyChanged(nameof(CanApplyHistoryRetention));
            }
        }
    }

    public bool CanApplyHistoryRetention =>
        CanManageHistoryRetention && HasPendingHistoryRetentionChange;

    public bool RequiresHistoryRetentionConfirmation =>
        _storedHistoryRetention is { } stored
        && SelectedHistoryRetentionOption is { } selected
        && (selected.Policy.MaximumEntries < stored.Policy.MaximumEntries
            || selected.Policy.MaximumAge < stored.Policy.MaximumAge);

    public string DefinitionBundleStatus
    {
        get => _definitionBundleStatus;
        private set => SetProperty(ref _definitionBundleStatus, value);
    }

    public IReadOnlyList<FileProviderProfileDescriptor> FileProviderProfiles =>
        _filePanelClient.Profiles;

    public IReadOnlyList<AiProviderProfileDescriptor> AiProviderProfiles =>
        _aiProviderRuntime?.Profiles ?? [];

    public bool HasAiProviders => AiProviderDefinitions.Count > 0;

    public bool HasNoAiProviders => !HasAiProviders;

    public bool HasMcpServers => McpServerDefinitions.Count > 0;

    public bool HasNoMcpServers => !HasMcpServers;

    public bool HasMcpEnvironmentSecretTargets => McpEnvironmentSecretTargets.Count > 0;

    public bool HasFileTransfers => FileTransfers.Count > 0;

    public bool HasNoFileTransfers => !HasFileTransfers;

    public int ActiveFileTransferCount =>
        FileTransfers.Count(transfer => transfer.IsActive);

    public int FailedFileTransferCount =>
        FileTransfers.Count(transfer => transfer.HasError);

    public string FileTransferStatusText
    {
        get
        {
            var active = FileTransfers.FirstOrDefault(transfer => transfer.IsActive);
            if (active is not null)
            {
                return ActiveFileTransferCount == 1
                    ? active.HasKnownProgress
                        ? $"Transfer · {active.ProgressPercent:0}%"
                        : "Transfer in progress"
                    : $"{ActiveFileTransferCount} transfers";
            }

            if (FailedFileTransferCount > 0)
            {
                return FailedFileTransferCount == 1
                    ? "1 transfer failed"
                    : $"{FailedFileTransferCount} transfers failed";
            }

            return "Transfers complete";
        }
    }

    public IReadOnlyList<SecretKind> SecretKinds { get; } = Enum.GetValues<SecretKind>();

    public ShellRoute Route
    {
        get => _route;
        private set
        {
            if (SetProperty(ref _route, value))
            {
                OnPropertyChanged(nameof(IsWorkspaceVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
            }
        }
    }


    public SettingsPage SettingsPage
    {
        get => _settingsPage;
        set
        {
            if (SetProperty(ref _settingsPage, value))
            {
                OnPropertyChanged(nameof(IsAppearanceSettingsVisible));
                OnPropertyChanged(nameof(IsWorkspaceSettingsVisible));
                OnPropertyChanged(nameof(IsKeybindingSettingsVisible));
                OnPropertyChanged(nameof(IsFilesSettingsVisible));
                OnPropertyChanged(nameof(IsTerminalSettingsVisible));
                OnPropertyChanged(nameof(IsQuickTerminalSettingsVisible));
                OnPropertyChanged(nameof(IsSecretsSettingsVisible));
                OnPropertyChanged(nameof(IsDiagnosticsSettingsVisible));
                OnPropertyChanged(nameof(IsAgentSettingsVisible));
                OnPropertyChanged(nameof(IsMcpSettingsVisible));
                OnPropertyChanged(nameof(IsAboutSettingsVisible));
                if (value is SettingsPage.Secrets or SettingsPage.Mcp)
                {
                    _ = RefreshSecretsAsync(CancellationToken.None);
                }
            }
        }
    }

    public ShellOverlay Overlay
    {
        get => _overlay;
        private set
        {
            if (SetProperty(ref _overlay, value))
            {
                _overlayRevision++;
                OnPropertyChanged(nameof(HasOverlay));
                OnPropertyChanged(nameof(IsCommandPaletteVisible));
                OnPropertyChanged(nameof(IsNewPanelVisible));
                OnPropertyChanged(nameof(IsLayoutDesignerVisible));
                OnPropertyChanged(nameof(IsDefinitionEditorVisible));
                OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
            }
        }
    }

    /// <summary>
    /// Every workspace that is open, not only the one on screen.
    ///
    /// Switching between them used to dispose the one being left: its sessions
    /// were killed and its tabs rebuilt from the definition on the way back,
    /// which is not what changing view means. A workspace now lives until it is
    /// closed, and <see cref="RuntimeWorkspace"/> names which of them is in
    /// front.
    /// </summary>
    public ReadOnlyObservableCollection<RuntimeWorkspaceViewModel> OpenWorkspaces { get; }

    /// <summary>
    /// Whether the shell has the user's attention. The window tells it; a
    /// notification arriving while the app is in the background always leaves a
    /// mark, because nobody was looking at anything.
    /// </summary>
    public bool IsWindowFocused
    {
        get => _isWindowFocused;
        set
        {
            if (SetProperty(ref _isWindowFocused, value) && value)
            {
                Notifications.MarkVisibleSeen();
            }
        }
    }

    private bool _isWindowFocused = true;

    internal ShellNotificationCenter Notifications { get; }

    private readonly ObservableCollection<RuntimeWorkspaceViewModel> _openWorkspaces = [];

    /// <summary>
    /// What each open workspace was opened from. The active one's is
    /// <see cref="_runtimeHistorySource"/>; this keeps the rest so a workspace
    /// can be found again by its definition instead of being opened twice.
    /// </summary>
    private readonly Dictionary<WorkspaceInstanceId, RuntimeHistorySource> _runtimeSources = [];

    public RuntimeWorkspaceViewModel? RuntimeWorkspace
    {
        get => _runtimeWorkspace;
        private set
        {
            var previous = _runtimeWorkspace;
            if (SetProperty(ref _runtimeWorkspace, value))
            {
                // Marked first because the notification above is not free: the
                // dock control, three tab strips and the status bar all re-read
                // the workspace from it, and Dock rebuilds its layout while
                // this setter is still running. Without a mark here that cost
                // would be charged to whatever came next.
                ShowCanvasOf(value, insteadOf: previous);
                _activation?.Mark("bindings");
                StopRuntimeGraphWatch();
                _activation?.Mark("graph stop");
                // One announcement, from the one place that knows what is in
                // front. Scattered across the open paths it was missing from
                // every other way of arriving — restore among them.
                SetActiveWorkspaceAccent(ShellAccentOf(value));
                _activation?.Mark("accent");

                StopTrackingAgentTerminalSelection(previous);
                StopTrackingRecovery(previous);

                // Only a workspace that has actually gone is torn down. One that
                // is merely no longer in front keeps its sessions, its panels,
                // and its place in the open set.
                if (previous is not null && !_openWorkspaces.Contains(previous))
                {
                    QueueRemainingRecentSessionCompletions(RecentSessionOutcome.GracefullyClosed);
                    previous.DisposePanels();
                }

                _runtimeHistorySource = value is null
                    ? null
                    : _runtimeSources.GetValueOrDefault(value.Id);
                SyncAgentPanelPlacement(value);
                StartTrackingRecovery(value);
                StartTrackingAgentTerminalSelection(value);
                _activation?.Mark("tracking");
                RefreshAgentTerminalSelectionOptions(resetSelection: true);
                _activation?.Mark("agent terminals");
                OnPropertyChanged(nameof(HasRuntimeWorkspace));
                OnPropertyChanged(nameof(NewItemLauncherTitle));
                OnPropertyChanged(nameof(CanCreateBrowserPanel));
                _activation?.Mark("notifications");
                RefreshLauncherSearchResults();
                _activation?.Mark("search results");
            }
        }
    }

    public bool HasRuntimeWorkspace => RuntimeWorkspace is not null;

    /// <summary>
    /// The accent the open workspace asks the shell to wear, or null to go back
    /// to the application's own. Raised rather than applied here: retinting the
    /// shell means republishing application resources, which is the host's job.
    /// </summary>
    public event EventHandler<string?>? WorkspaceAccentChanged;

    private string? _activeWorkspaceAccent;

    /// <summary>
    /// The accent the open workspace is asking the shell to wear, for anything
    /// that starts listening after it was announced.
    /// </summary>
    public string? ActiveWorkspaceAccent => _activeWorkspaceAccent;

    /// <summary>
    /// Says which part of bringing a workspace forward cost the time, when any
    /// of it did. Under the budget it says nothing; a switch that is not felt
    /// is not worth a line.
    /// </summary>
    private static void ReportSwitchPhases(
        string workspace,
        long autoSaveMilliseconds,
        long activationMilliseconds,
        long snapshotMilliseconds)
    {
        const long budgetMilliseconds = 32;
        var total = autoSaveMilliseconds + activationMilliseconds + snapshotMilliseconds;
        if (total < budgetMilliseconds)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[ghostshell:perf] bringing '{workspace}' forward took {total} ms — "
            + $"autosave flush {autoSaveMilliseconds} ms, activation "
            + $"{activationMilliseconds} ms, recovery snapshot "
            + $"{snapshotMilliseconds} ms");
    }

    /// <summary>
    /// Brings one workspace's canvas forward without ever showing an empty one.
    ///
    /// A dock control builds only the layout it is showing — one that is not
    /// shown has no visual tree at all, whatever it is given and however long it
    /// waits — so the arriving canvas cannot be prepared out of sight. It is
    /// shown straight away and the departing one is left on top of it, covering
    /// it whole, until it has had frames enough to build. Then the cover comes
    /// off and what is underneath is finished.
    /// </summary>
    private static void ShowCanvasOf(
        RuntimeWorkspaceViewModel? workspace,
        RuntimeWorkspaceViewModel? insteadOf)
    {
        if (workspace is not null)
        {
            workspace.CanvasDepth = 0;
            workspace.IsCanvasShown = true;
        }

        if (insteadOf is null || ReferenceEquals(insteadOf, workspace))
        {
            return;
        }

        if (workspace is null)
        {
            insteadOf.IsCanvasShown = false;
            insteadOf.CanvasDepth = 0;
            return;
        }

        insteadOf.CanvasDepth = 1;
        RetireCanvas(insteadOf, CoveringCanvasFrames);
    }

    /// <summary>
    /// How long the departing canvas keeps covering the arriving one. Frames
    /// rather than milliseconds, because what is being waited for is a dock
    /// control building its tree, which happens across passes. Three, because
    /// the measurement that found this showed the rebuild landing after the
    /// frame following the swap.
    /// </summary>
    private const int CoveringCanvasFrames = 3;

    private static void RetireCanvas(
        RuntimeWorkspaceViewModel canvas,
        int framesRemaining)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                // It came back to the front while it was covering; it is not
                // retiring any more.
                if (canvas.CanvasDepth == 0)
                {
                    return;
                }

                if (framesRemaining > 0)
                {
                    RetireCanvas(canvas, framesRemaining - 1);
                    return;
                }

                canvas.IsCanvasShown = false;
                canvas.CanvasDepth = 0;
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void SetActiveWorkspaceAccent(string? accent)
    {
        var next = string.IsNullOrWhiteSpace(accent) ? null : accent;
        if (string.Equals(_activeWorkspaceAccent, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeWorkspaceAccent = next;
        WorkspaceAccentChanged?.Invoke(this, next);
    }

    public string NewItemLauncherTitle => HasRuntimeWorkspace ? "New Tab" : "New Session";

    public bool CanStartBrowserSession => _browserRendererViewFactory is not null;

    public bool CanCreateBrowserPanel =>
        CanStartBrowserSession
        && RuntimeWorkspace?.ActiveTab is not null;

    public RuntimePanelViewModel? ActivePanel => RuntimeWorkspace?.ActiveTab?.ActivePanel;

    public Task SendAgentPromptAsync(CancellationToken cancellationToken = default)
    {
        if (AgentChat is not { } agentChat)
        {
            return Task.CompletedTask;
        }

        if (agentChat.IsSteeringAvailable)
        {
            return agentChat.SteerAsync(cancellationToken);
        }

        if (RuntimeWorkspace is not { ActiveTab: { } activeTab } workspace)
        {
            agentChat.ReportTargetUnavailable(
                "Open a terminal, browser, File Viewer, or Process Monitor panel "
                + "before sending a request to the agent.");
            return Task.CompletedTask;
        }

        AgentTarget target;
        switch (SelectedAgentRunScope.Kind)
        {
            case AgentRunScopeKind.ActivePanel:
                if (activeTab.ActivePanel is not { } activePanel
                    || !IsAgentCapablePanel(activePanel))
                {
                    agentChat.ReportTargetUnavailable(
                        "Select an active terminal, browser, File Viewer, or hosted "
                        + "Process Monitor panel, "
                        + "or choose a broader agent scope.");
                    return Task.CompletedTask;
                }

                target = new AgentTarget.Panel(
                    WindowId,
                    workspace.Id,
                    activeTab.Id,
                    activePanel.Id);
                break;
            case AgentRunScopeKind.CurrentTab:
                target = new AgentTarget.OpenTab(
                    WindowId,
                    workspace.Id,
                    activeTab.Id);
                break;
            case AgentRunScopeKind.Workspace:
                target = new AgentTarget.Workspace(
                    WindowId,
                    workspace.Id);
                break;
            case AgentRunScopeKind.SelectedPanels:
                if (!TryCreateSelectedPanelsTarget(
                        workspace,
                        out target,
                        out var selectionError))
                {
                    agentChat.ReportTargetUnavailable(selectionError);
                    return Task.CompletedTask;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(SelectedAgentRunScope),
                    SelectedAgentRunScope.Kind,
                    "The selected agent scope is not supported.");
        }

        if (!TryResolveAgentPolicy(workspace, target, out var policy, out var policyError))
        {
            agentChat.ReportTargetUnavailable(policyError);
            return Task.CompletedTask;
        }

        return policy is null
            ? agentChat.SendAsync(target, cancellationToken)
            : agentChat.SendAsync(target, policy, cancellationToken);
    }

    private static bool TryResolveAgentPolicy(
        RuntimeWorkspaceViewModel workspace,
        AgentTarget target,
        out AgentPolicy? policy,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(target);
        RuntimeTabViewModel[] tabs = target switch
        {
            AgentTarget.Panel panel =>
                workspace.Tabs.Where(tab => tab.Id == panel.TabId).ToArray(),
            AgentTarget.OpenTab openTab =>
                workspace.Tabs.Where(tab => tab.Id == openTab.TabId).ToArray(),
            AgentTarget.Workspace =>
                workspace.Tabs.ToArray(),
            AgentTarget.SelectedPanels selected
                when selected.Panels.All(panel =>
                    workspace.Tabs.Any(tab =>
                        tab.Id == panel.TabId
                        && tab.Panels.Any(candidate => candidate.Id == panel.PanelId))) =>
                selected.Panels
                    .Select(panel => workspace.Tabs.Single(tab => tab.Id == panel.TabId))
                    .Distinct()
                    .ToArray(),
            _ => [],
        };
        if (tabs.Length == 0)
        {
            policy = null;
            error = "The selected agent scope no longer has trusted runtime policy provenance.";
            return false;
        }

        var overrideCount = tabs.Count(tab => tab.AgentPolicy.HasPolicyOverride);
        if (overrideCount == 0)
        {
            policy = null;
            error = string.Empty;
            return true;
        }

        if (overrideCount != tabs.Length)
        {
            policy = null;
            error =
                "The selected scope combines saved policy overrides with inherited provider settings. "
                + "Choose a narrower scope.";
            return false;
        }

        var policies = tabs
            .Select(tab => tab.AgentPolicy.EffectivePolicy)
            .ToArray();
        var first = policies[0];
        if (policies.Any(candidate =>
                !string.Equals(candidate.Provider, first.Provider, StringComparison.Ordinal)
                || !string.Equals(candidate.Model, first.Model, StringComparison.Ordinal)))
        {
            policy = null;
            error =
                "The selected scope combines different saved agent policy providers or models. "
                + "Choose a narrower scope.";
            return false;
        }

        try
        {
            policy = AgentPolicyResolver.ResolveLeastPrivilege(policies);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            policy = null;
            error =
                "The selected scope does not have a valid durable agent policy. "
                + "Choose a narrower scope.";
            return false;
        }
    }

    private bool TryCreateSelectedPanelsTarget(
        RuntimeWorkspaceViewModel workspace,
        out AgentTarget target,
        out string error)
    {
        target = null!;
        if (_agentTerminalSelectionStale)
        {
            error =
                "A selected terminal is no longer live. Review the selected terminals before sending.";
            SetAgentTerminalSelectionError(error, stale: true);
            return false;
        }

        var selected = AgentTerminalSelectionOptions
            .Where(option => option.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            error = "Select at least one live terminal before sending.";
            SetAgentTerminalSelectionError(error, stale: false);
            return false;
        }

        if (selected.Length > AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            error =
                $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.";
            SetAgentTerminalSelectionError(error, stale: false);
            return false;
        }

        var panels = new List<AgentTarget.Panel>(selected.Length);
        foreach (var option in selected)
        {
            var tab = workspace.Tabs.SingleOrDefault(
                candidate => candidate.Id == option.TabId);
            var terminal = tab?.Panels.SingleOrDefault(
                candidate => candidate.Id == option.PanelId)
                as TerminalRuntimePanelViewModel;
            if (tab is null || terminal is null || !IsLiveAgentTerminal(terminal))
            {
                error =
                    "A selected terminal is no longer live. Review the selected terminals before sending.";
                SetAgentTerminalSelectionError(error, stale: true);
                return false;
            }

            panels.Add(
                new AgentTarget.Panel(
                    WindowId,
                    workspace.Id,
                    tab.Id,
                    terminal.Id));
        }

        target = new AgentTarget.SelectedPanels(panels);
        error = string.Empty;
        HasAgentTerminalSelectionError = false;
        UpdateAgentTerminalSelectionStatus();
        return true;
    }

    private static bool IsLiveAgentTerminal(
        TerminalRuntimePanelViewModel terminal) =>
        terminal.ConnectionState == ConnectionPanelState.Ready
        && terminal.SessionRequest is not null
        && terminal.HasObservedActiveSession;

    private static bool IsAgentCapablePanel(RuntimePanelViewModel panel) =>
        panel is TerminalRuntimePanelViewModel
            or BrowserRuntimePanelViewModel
            or FileRuntimePanelViewModel
            or ProcessMonitorRuntimePanelViewModel
        {
            HasHostedSession: true,
        };

    public CommandContext ActiveCommandContexts
    {
        get
        {
            var contexts = CommandContext.Global | CommandContext.Window;
            if (!IsWorkspaceVisible
                || RuntimeWorkspace is not { ActiveTab: { } activeTab })
            {
                return HasOverlay ? contexts | CommandContext.Modal : contexts;
            }

            contexts |= CommandContext.Workspace | CommandContext.Tab;
            if (activeTab.ActivePanel is { } activePanel)
            {
                contexts |= CommandContext.Panel;
                contexts |= activePanel.Kind switch
                {
                    PanelKind.Terminal => CommandContext.Terminal,
                    PanelKind.Browser => CommandContext.Browser,
                    _ => CommandContext.None,
                };
            }

            return HasOverlay ? contexts | CommandContext.Modal : contexts;
        }
    }

    public bool IsWorkspaceVisible => Route == ShellRoute.Workspace;

    public bool IsSettingsVisible => Route == ShellRoute.Settings;

    public bool IsWorkspaceCanvasVisible => IsWorkspaceVisible && !HasOverlay;

    public bool IsAppearanceSettingsVisible => SettingsPage == SettingsPage.Appearance;

    public bool IsWorkspaceSettingsVisible => SettingsPage == SettingsPage.Workspaces;

    public bool RestoreSessionsOnStart
    {
        get => _restoreSessionsOnStart;
        private set => SetProperty(ref _restoreSessionsOnStart, value);
    }

    public bool CanChangeRestoreSessionsOnStart =>
        _sessionRestoreCoordinator is null
        || (_sessionRestorePreferenceLoaded && !_sessionRestorePreferenceSaving);

    public bool UseTerminalMultiplexingForSshTerminals =>
        _terminalMultiplexingMode == TerminalMultiplexingMode.Automatic;

    public bool CanChangeTerminalMultiplexing =>
        _terminalMultiplexerCoordinator is null
        || (_terminalMultiplexingPreferenceLoaded && !_terminalMultiplexingPreferenceSaving);

    public bool HasManagedRemoteSessions => ManagedRemoteSessions.Count > 0;

    public bool IsKeybindingSettingsVisible => SettingsPage == SettingsPage.Keybindings;

    public bool IsFilesSettingsVisible => SettingsPage == SettingsPage.Files;

    public bool IsTerminalSettingsVisible => SettingsPage == SettingsPage.Terminal;

    public bool IsQuickTerminalSettingsVisible => SettingsPage == SettingsPage.QuickTerminal;

    public bool IsSecretsSettingsVisible => SettingsPage == SettingsPage.Secrets;

    public bool IsDiagnosticsSettingsVisible => SettingsPage == SettingsPage.Diagnostics;

    public bool IsAgentSettingsVisible => SettingsPage == SettingsPage.Agent;

    public bool IsMcpSettingsVisible => SettingsPage == SettingsPage.Mcp;

    public bool IsAboutSettingsVisible => SettingsPage == SettingsPage.About;

    public string SecretVaultStatus
    {
        get => _secretVaultStatus;
        private set => SetProperty(ref _secretVaultStatus, value);
    }

    public bool HasOverlay => Overlay != ShellOverlay.None;

    public bool IsCommandPaletteVisible => Overlay == ShellOverlay.CommandPalette;

    public bool IsNewPanelVisible => Overlay == ShellOverlay.NewPanel;

    public bool IsLayoutDesignerVisible => Overlay == ShellOverlay.LayoutDesigner;

    public bool IsDefinitionEditorVisible => Overlay == ShellOverlay.DefinitionEditor;

    public string EditorTitle => _editingDefinition?.Kind == WorkspaceDefinition.Kind
        ? "Edit workspace"
        : "Edit saved screen";

    public string EditorName
    {
        get => _editorName;
        set => SetProperty(ref _editorName, value);
    }

    public string EditorDescription
    {
        get => _editorDescription;
        set => SetProperty(ref _editorDescription, value);
    }

    public bool IsAgentPanelVisible
    {
        get => _isAgentPanelVisible;
        set
        {
            if (SetProperty(ref _isAgentPanelVisible, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
            }
        }
    }

    /// <summary>
    /// Whether the agent panel holds a slot in the layout rather than floating
    /// over the canvas. Per workspace: read from its definition when it comes
    /// to the front, written back when the pin is toggled.
    /// </summary>
    public bool IsAgentPanelDocked
    {
        get => _isAgentPanelDocked;
        private set
        {
            if (SetProperty(ref _isAgentPanelDocked, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
                OnPropertyChanged(nameof(AgentPanelPinTip));
            }
        }
    }

    /// <summary>The layout reserves the agent panel's width only while a pinned panel is on screen.</summary>
    public bool IsAgentPanelDockedVisible => IsAgentPanelVisible && IsAgentPanelDocked;

    public string AgentPanelPinTip => IsAgentPanelDocked
        ? "Unpin — float over the workspace"
        : "Pin to the workspace layout";

    public string LauncherSearchQuery
    {
        get => _launcherSearchQuery;
        set
        {
            if (SetProperty(ref _launcherSearchQuery, value))
            {
                OnPropertyChanged(nameof(LauncherSearchEmptyState));
                RefreshLauncherSearchResults(preserveSelection: false);
            }
        }
    }

    public LauncherSearchResultViewModel? SelectedLauncherSearchResult
    {
        get => _selectedLauncherSearchResult;
        set => SetProperty(ref _selectedLauncherSearchResult, value);
    }

    public string HistorySearchQuery
    {
        get => _historySearchQuery;
        set
        {
            if (SetProperty(ref _historySearchQuery, value))
            {
                RefreshHistorySearchResults(preserveSelection: false);
            }
        }
    }

    public RecentSessionHistoryItemViewModel? SelectedHistorySession
    {
        get => _selectedHistorySession;
        set
        {
            if (SetProperty(ref _selectedHistorySession, value))
            {
                OnPropertyChanged(nameof(HasSelectedHistorySession));
                OnPropertyChanged(nameof(HasNoSelectedHistorySession));
            }
        }
    }

    public bool HasSelectedHistorySession => SelectedHistorySession is not null;

    public bool HasNoSelectedHistorySession => !HasSelectedHistorySession;

    public HistoryExportScope SelectedHistoryExportScope
    {
        get => _selectedHistoryExportScope;
        set => SetProperty(ref _selectedHistoryExportScope, value);
    }

    public IReadOnlyList<RecentSessionRecord> CaptureHistoryExportSnapshot() =>
        (SelectedHistoryExportScope == HistoryExportScope.CurrentResults
            ? FilteredHistorySessions
            : HistorySessions)
        .Select(item => item.Record)
        .ToArray();

    public void SetHistoryExportStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        HistoryExportStatus = status.Trim();
    }

    public bool TryBeginHistoryExport(HistoryExportScope scope)
    {
        var canBegin = scope switch
        {
            HistoryExportScope.AllRetained => CanExportAllHistory,
            HistoryExportScope.CurrentResults => CanExportFilteredHistory,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
        if (!canBegin)
        {
            return false;
        }

        SelectedHistoryExportScope = scope;
        IsHistoryExporting = true;
        HistoryExportStatus = "Preparing the metadata-only history export…";
        return true;
    }

    public void EndHistoryExport(string status)
    {
        SetHistoryExportStatus(status);
        IsHistoryExporting = false;
    }

    public async Task<bool> RetryRecentSessionHistoryAsync(
        CancellationToken cancellationToken)
    {
        if (!CanRetryRecentSessionHistory)
        {
            return false;
        }

        IsHistoryLoading = true;
        try
        {
            var operation = QueueHistoryOperation(token =>
                RefreshRecentSessionsCoreAsync(token));
            await operation.WaitAsync(cancellationToken);
            if (HasRecentSessionFailure)
            {
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    public void SelectFirstAvailableLauncherSearchResult()
    {
        var index = LauncherSearchProjection.FindNextAvailableIndex(
            LauncherSearchResults,
            currentIndex: -1,
            direction: 1);
        SelectedLauncherSearchResult = index < 0 ? null : LauncherSearchResults[index];
    }

    public void MoveLauncherSearchSelection(int direction)
    {
        var currentIndex = SelectedLauncherSearchResult is null
            ? -1
            : LauncherSearchResults.IndexOf(SelectedLauncherSearchResult);
        var nextIndex = LauncherSearchProjection.FindNextAvailableIndex(
            LauncherSearchResults,
            currentIndex,
            direction);
        SelectedLauncherSearchResult = nextIndex < 0 ? null : LauncherSearchResults[nextIndex];
    }

    public LauncherSearchTarget? ConfirmLauncherSearchSelection() =>
        LauncherSearchProjection.ConfirmSelection(SelectedLauncherSearchResult);

    public string? OperationError
    {
        get => _operationError;
        private set
        {
            if (SetProperty(ref _operationError, value))
            {
                OnPropertyChanged(nameof(HasOperationError));
            }
        }
    }

    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);

    public string TabReorderStatus
    {
        get => _tabReorderStatus;
        private set => SetProperty(ref _tabReorderStatus, value);
    }

    public string? ApplicationKeySequenceHint
    {
        get => _applicationKeySequenceHint;
        private set
        {
            if (SetProperty(ref _applicationKeySequenceHint, value))
            {
                OnPropertyChanged(nameof(HasApplicationKeySequenceHint));
            }
        }
    }

    public bool HasApplicationKeySequenceHint =>
        !string.IsNullOrWhiteSpace(ApplicationKeySequenceHint);

    public string CommandPaletteShortcut =>
        QuickTerminalHotkeyText.FormatApplicationCommand("K");

    public string LauncherShortcutSummary =>
        $"{QuickTerminalHotkeyText.FormatApplicationCommand("1")} launcher   " +
        $"{QuickTerminalHotkeyText.FormatApplicationCommand(",")} settings   " +
        $"{CommandPaletteShortcut} search";

    public string CommandPaletteAction => $"{CommandPaletteShortcut}  Search";

    public string CommandPaletteSettingsAction =>
        $"{CommandPaletteShortcut}  Search & commands";

    public ThemePreference ActiveTheme => _catalog.Snapshot.Themes
        .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)?.Value
        ?? ThemePreference.Default;

    public TerminalProfile? ActiveTerminalProfile =>
        _catalog.Snapshot.TerminalProfiles.FirstOrDefault()?.Value;

    public KeymapProfile ActiveApplicationKeymap =>
        ResolveActiveApplicationKeymap(_catalog.Snapshot).Value;

    public long ActiveApplicationKeymapRevision =>
        ResolveActiveApplicationKeymap(_catalog.Snapshot).Revision;

    public string ActiveApplicationKeymapName => ActiveApplicationKeymap.Name;

    public TerminalProfileEditorViewModel? TerminalSettingsEditor
    {
        get => _terminalSettingsEditor;
        private set => SetProperty(ref _terminalSettingsEditor, value);
    }

    public QuickTerminalSettingsEditorViewModel? QuickTerminalSettingsEditor
    {
        get => _quickTerminalSettingsEditor;
        private set => SetProperty(ref _quickTerminalSettingsEditor, value);
    }

    public string ThemeMode => ActiveTheme.Appearance.ToString();

    public string ThemeProfile => ActiveTheme.PlatformProfile.ToString();

    public string ThemeTextScale => ActiveTheme.TextScaleOverride is { } textScale
        ? textScale.ToString("0.##%", System.Globalization.CultureInfo.InvariantCulture)
        : "Follow host";

    /// <summary>Window-chrome settings the shell layout binds to directly.</summary>
    public bool ShowTabBar => ActiveTheme.ShowTabBar;

    /// <summary>
    /// Whether the workspace rail is shown.
    ///
    /// Settable, so a switch can bind to it both ways like every other toggle
    /// in the shell. Bound one way and driven by a changed event instead, the
    /// binding pushed the stored value back over the user's flip and the switch
    /// sprang shut again.
    ///
    /// Writing it saves only this field: a surface showing one switch must not
    /// carry the rest of the chrome back to its defaults on the way past.
    /// </summary>
    public bool ShowWorkspacesPanel
    {
        get => ActiveTheme.ShowWorkspacesPanel;
        set
        {
            var theme = ActiveTheme;
            if (value == theme.ShowWorkspacesPanel)
            {
                return;
            }

            _ = SaveThemeAsync(
                theme.Appearance,
                theme.PlatformProfile,
                theme.Accent,
                theme.TextScaleOverride,
                CancellationToken.None,
                ThemeChromePreference.From(theme) with { ShowWorkspacesPanel = value });
        }
    }

    public bool IsWorkspacePanelOnLeft =>
        ActiveTheme.WorkspacePanelPlacement == WorkspacePanelPlacement.Left;

    public bool IsWorkspacePanelOnRight => !IsWorkspacePanelOnLeft;

    /// <summary>The rail's dock edge, so the setting moves the real panel.</summary>
    public Avalonia.Controls.Dock WorkspacePanelDock => IsWorkspacePanelOnLeft
        ? Avalonia.Controls.Dock.Left
        : Avalonia.Controls.Dock.Right;

    public bool IsTabStripVisibleOnTop =>
        ShowTabBar && ActiveTheme.TabStripPlacement == TabStripPlacement.Top;

    public bool IsTabStripVisibleOnBottom =>
        ShowTabBar && ActiveTheme.TabStripPlacement == TabStripPlacement.Bottom;

    /// <summary>A side strip is one control docked to whichever edge is chosen.</summary>
    public bool IsTabStripVisibleOnSide =>
        ShowTabBar && ActiveTheme.TabStripPlacement
            is TabStripPlacement.Left or TabStripPlacement.Right;

    public Avalonia.Controls.Dock TabStripDock =>
        ActiveTheme.TabStripPlacement == TabStripPlacement.Right
            ? Avalonia.Controls.Dock.Right
            : Avalonia.Controls.Dock.Left;

    /// <summary>Which side the strip touches, as booleans, because the strip's
    /// floating margin belongs on the window side only — the canvas supplies
    /// the gap on the inner side, the same contract the other sidebars keep.</summary>
    public bool IsTabStripDockedLeft =>
        IsTabStripVisibleOnSide && TabStripDock == Avalonia.Controls.Dock.Left;

    public bool IsTabStripDockedRight =>
        IsTabStripVisibleOnSide && TabStripDock == Avalonia.Controls.Dock.Right;

    public Avalonia.Controls.PlacementMode SideTabIconPickerPlacement =>
        IsTabStripDockedRight
            ? Avalonia.Controls.PlacementMode.LeftEdgeAlignedTop
            : Avalonia.Controls.PlacementMode.RightEdgeAlignedTop;

    public string ThemeAccent => ActiveTheme.Accent.Kind == AccentPreferenceKind.Custom
        ? ActiveTheme.Accent.CustomColor?.ToString() ?? ThemePreference.BronzeFallback.ToString()
        : "Follow system accent";

    public int KeybindingConflictCount => Keybindings.Count(item => item.HasConflict);

    public void ShowSettings(SettingsPage page = SettingsPage.Appearance)
    {
        if (!TryDismissOverlayForNavigation())
        {
            return;
        }

        SettingsPage = page;
        Route = ShellRoute.Settings;
        if (page == SettingsPage.Keybindings)
        {
            EnsureKeybindingEditor();
        }

        if (page == SettingsPage.Files)
        {
            // The usage figure is read when the page is opened, not on a
            // timer: a settings page is looked at, not watched.
            FilePreviewSettingsEditor.RefreshCacheUsage();
        }
    }

    public void ShowWorkspace()
    {
        if (RuntimeWorkspace is not null && TryDismissOverlayForNavigation())
        {
            Route = ShellRoute.Workspace;
        }
    }

    public void ShowOverlay(ShellOverlay overlay)
    {
        if (Overlay == ShellOverlay.LayoutDesigner
            && overlay != ShellOverlay.LayoutDesigner
            && LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the layout changes before opening another overlay.");
            return;
        }

        if (Overlay == ShellOverlay.DefinitionEditor
            && overlay != ShellOverlay.DefinitionEditor
            && WorkspaceEditor?.RequestCancel()
                == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the workspace changes before opening another overlay.");
            return;
        }

        Overlay = overlay;
        if (overlay == ShellOverlay.CommandPalette)
        {
            LauncherSearchQuery = string.Empty;
            RefreshLauncherSearchResults(preserveSelection: false);
        }
    }

    public void CloseOverlay() => Overlay = ShellOverlay.None;

    public void DismissWorkspaceEditor()
    {
        WorkspaceEditor = null;
        _editingDefinition = null;
        _editingRevision = null;
        if (Overlay == ShellOverlay.DefinitionEditor)
        {
            Overlay = ShellOverlay.None;
        }
    }

    public void BeginCreateLayout()
    {
        if (!CanReplaceLayoutDesigner())
        {
            return;
        }

        ClearError();
        LayoutDesignerEditor = LayoutDesignerViewModel.CreateNew();
        Overlay = ShellOverlay.LayoutDesigner;
    }

    public void BeginEditLayout(LayoutId id)
    {
        if (!CanReplaceLayoutDesigner())
        {
            return;
        }

        var stored = _catalog.Snapshot.Layouts.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            SetError("That layout no longer exists.");
            return;
        }

        ClearError();
        LayoutDesignerEditor = new LayoutDesignerViewModel(stored.Value, stored.Revision);
        Overlay = ShellOverlay.LayoutDesigner;
    }

    public void DismissLayoutDesigner()
    {
        LayoutDesignerEditor = null;
        if (Overlay == ShellOverlay.LayoutDesigner)
        {
            Overlay = ShellOverlay.None;
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
        SaveLayoutDesignerAsync(CancellationToken cancellationToken)
    {
        ClearError();
        if (LayoutDesignerEditor is null)
        {
            return Fail<StoredDefinition<LayoutDefinition>>(
                "Open the layout designer before saving a layout.");
        }

        LayoutDesignerSaveRequest request;
        try
        {
            request = LayoutDesignerEditor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail<StoredDefinition<LayoutDefinition>>(exception.Message);
        }

        var result = await _catalog.SaveLayoutAsync(
            request.Definition,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    private bool CanReplaceLayoutDesigner()
    {
        if (LayoutDesignerEditor?.RequestCancel()
            != LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            return true;
        }

        SetError("Save or discard the current layout changes first.");
        return false;
    }

    public void SelectKeybindingProfile(KeybindingProfileItemViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var stored = _catalog.Snapshot.Keymaps.SingleOrDefault(item => item.Value.Id == profile.Id);
        if (stored is null)
        {
            SetError("That keybinding profile no longer exists.");
            return;
        }

        ClearError();
        OpenKeybindingEditor(stored.Value, stored.Revision, profile.IsBuiltIn);
        SelectedKeybindingProfile = profile;
    }

    public void CloneSelectedKeybindingProfile()
    {
        if (SelectedKeybindingProfile is not { IsBuiltIn: true } selected)
        {
            SetError("Select a built-in keybinding preset to clone.");
            return;
        }

        var source = _catalog.Snapshot.Keymaps
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == selected.Id);
        if (source is null)
        {
            SetError("That built-in keybinding preset no longer exists.");
            return;
        }

        var cloneId = new KeymapProfileId($"user.keymap.{Guid.NewGuid():N}");
        var cloneName = $"{source.Name} copy";
        var editor = KeybindingSettingsEditor.ClonePreset(
            source,
            cloneId,
            cloneName,
            BuiltInCommands.Registry);
        var profile = new KeybindingProfileItemViewModel(
            cloneId,
            Revision: null,
            cloneName,
            source.Layer,
            IsBuiltIn: false,
            IsUnsaved: true);
        KeybindingProfiles.Add(profile);
        SelectedKeybindingProfile = profile;
        KeybindingEditorSession = new KeybindingEditorSessionViewModel(
            editor,
            isReadOnly: false);
        ClearError();
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>>
        SaveKeybindingEditorAsync(CancellationToken cancellationToken)
    {
        ClearError();
        if (KeybindingEditorSession is null)
        {
            return Fail<StoredDefinition<KeymapProfile>>(
                "Select a keybinding profile before saving.");
        }

        KeybindingSettingsSaveRequest request;
        try
        {
            request = KeybindingEditorSession.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail<StoredDefinition<KeymapProfile>>(exception.Message);
        }

        var result = await _catalog.SaveKeymapAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        if (result is { IsSuccess: true, Value: { } saved })
        {
            RefreshKeybindings(_catalog.Snapshot);
            var profile = KeybindingProfiles.SingleOrDefault(item => item.Id == saved.Value.Id)
                ?? new KeybindingProfileItemViewModel(
                    saved.Value.Id,
                    saved.Revision,
                    saved.Value.Name,
                    saved.Value.Layer,
                    IsBuiltInKeymap(saved.Value.Id),
                    IsUnsaved: false);
            if (KeybindingProfiles.All(item => item.Id != profile.Id))
            {
                KeybindingProfiles.Add(profile);
            }

            OpenKeybindingEditor(saved.Value, saved.Revision, profile.IsBuiltIn);
            SelectedKeybindingProfile = profile;
        }

        return result;
    }

    public void BeginEditWorkspace(WorkspaceId id)
    {
        var stored = _catalog.Snapshot.Workspaces.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            SetError("That workspace no longer exists.");
            return;
        }

        if (WorkspaceEditor?.RequestCancel()
            == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the current workspace changes before editing another workspace.");
            return;
        }

        var snapshot = _catalog.Snapshot;
        WorkspaceEditor = new WorkspaceEditorViewModel(
            stored.Value,
            stored.Revision,
            snapshot.Connections.Select(item => item.Value).ToArray(),
            snapshot.Screens.Select(item => item.Value).ToArray(),
            snapshot.Layouts.Select(item => item.Value).ToArray(),
            snapshot.FileProviderProfiles.Select(item => item.Value).ToArray());
        WorkspaceEditor.SetPeers(snapshot.Workspaces.Select(item => item.Value).ToArray());
        _editingDefinition = stored.Value.Key;
        _editingRevision = stored.Revision;
        EditorName = stored.Value.Name;
        EditorDescription = stored.Value.Description ?? string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        ClearError();
        Overlay = ShellOverlay.DefinitionEditor;
    }

    /// <summary>
    /// Opens the workspace editor over a fresh unsaved definition. Nothing is
    /// persisted until the editor saves, so cancelling leaves no orphan.
    /// </summary>
    public void BeginCreateWorkspace()
    {
        if (WorkspaceEditor?.RequestCancel()
            == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError(
                "Save or discard the current workspace changes before creating another workspace.");
            return;
        }

        var snapshot = _catalog.Snapshot;
        var definition = new WorkspaceDefinition(
            WorkspaceId.New(),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Untitled workspace",
            description: null,
            ThemePreference.BronzeFallback.ToString(),
            []);
        WorkspaceEditor = new WorkspaceEditorViewModel(
            definition,
            expectedRevision: null,
            snapshot.Connections.Select(item => item.Value).ToArray(),
            snapshot.Screens.Select(item => item.Value).ToArray(),
            snapshot.Layouts.Select(item => item.Value).ToArray(),
            snapshot.FileProviderProfiles.Select(item => item.Value).ToArray());
        WorkspaceEditor.SetPeers(snapshot.Workspaces.Select(item => item.Value).ToArray());
        _editingDefinition = definition.Key;
        _editingRevision = null;
        EditorName = definition.Name;
        EditorDescription = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        ClearError();
        Overlay = ShellOverlay.DefinitionEditor;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorAsync(CancellationToken cancellationToken)
    {
        ClearError();
        if (WorkspaceEditor is null)
        {
            return Fail<StoredDefinition<WorkspaceDefinition>>(
                "Choose a workspace to edit before saving.");
        }

        WorkspaceEditorSaveRequest request;
        try
        {
            request = WorkspaceEditor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail<StoredDefinition<WorkspaceDefinition>>(exception.Message);
        }

        return await SaveWorkspaceEditorAsync(request, cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorAsync(
            WorkspaceEditorSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        if (WorkspaceEditor is null
            || WorkspaceEditor.Id != request.Definition.Id
            || WorkspaceEditor.ExpectedRevision != request.ExpectedRevision)
        {
            return Fail<StoredDefinition<WorkspaceDefinition>>(
                "The workspace editor changed before the save could begin.");
        }

        var saved = await _catalog.SaveWorkspaceAsync(
            request.Definition,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(saved.Error);
        if (saved.IsSuccess)
        {
            ApplyTerminalMultiplexingOverrideToOpenWorkspaces(request.Definition);
            DismissWorkspaceEditor();
        }

        return saved;
    }

    private void ApplyTerminalMultiplexingOverrideToOpenWorkspaces(
        WorkspaceDefinition definition)
    {
        foreach (var runtime in _openWorkspaces)
        {
            if (!_runtimeSources.TryGetValue(runtime.Id, out var source)
                || source.SourceDefinition != definition.Key)
            {
                continue;
            }

            runtime.TerminalMultiplexingMode = definition.TerminalMultiplexingOverride;
            if (definition.TerminalMultiplexingOverride is { } mode)
            {
                _workspaceTerminalMultiplexingModes[runtime.Id] = mode;
            }
            else
            {
                _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            }
        }
    }

    public void BeginEditScreen(ScreenId id)
    {
        var stored = _catalog.Snapshot.Screens.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            SetError("That saved screen no longer exists.");
            return;
        }

        BeginEdit(stored.Value.Key, stored.Revision, stored.Value.Name, stored.Value.Description);
    }

    public void ToggleAgentPanel() => IsAgentPanelVisible = !IsAgentPanelVisible;

    /// <summary>
    /// Reads the front workspace's saved pin. A pinned panel is part of the
    /// layout and comes up with it; an unpinned one is a flyout, summoned when
    /// asked for — so arriving anywhere starts it hidden.
    /// </summary>
    private void SyncAgentPanelPlacement(RuntimeWorkspaceViewModel? workspace)
    {
        var pinned = FrontWorkspaceDefinition(workspace)?.Value.AgentPanelPinned == true;
        IsAgentPanelDocked = pinned;
        IsAgentPanelVisible = pinned;
    }

    /// <summary>
    /// Flips between the docked slot and the floating flyout, and writes the
    /// choice onto the workspace it was made in. A workspace with no saved
    /// definition behind it — a local browser, an ad-hoc database — keeps the
    /// choice for as long as it is open, which is all it has.
    /// </summary>
    public async Task ToggleAgentPanelPinAsync(CancellationToken cancellationToken)
    {
        var docked = !IsAgentPanelDocked;
        IsAgentPanelDocked = docked;
        IsAgentPanelVisible = true;

        if (FrontWorkspaceDefinition(RuntimeWorkspace) is not { } stored
            || stored.Value.AgentPanelPinned == docked)
        {
            return;
        }

        var current = stored.Value;
        var updated = new WorkspaceDefinition(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.Accent,
            current.Entries,
            current.AgentPolicyOverride,
            current.Icon,
            current.AutoSave,
            current.Color,
            docked);
        var saved = await _catalog.SaveWorkspaceAsync(updated, stored.Revision, cancellationToken);
        ApplyError(saved.Error);
    }

    private StoredDefinition<WorkspaceDefinition>? FrontWorkspaceDefinition(
        RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null
            || !_runtimeSources.TryGetValue(workspace.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        return _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == source.SourceDefinition);
    }

    public async Task<bool> OpenWorkspaceAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var storedWorkspace = _catalog.Snapshot.Workspaces
            .SingleOrDefault(item => item.Value.Id == workspaceId);
        if (storedWorkspace is null)
        {
            SetError("That workspace no longer exists.");
            return false;
        }

        var workspace = storedWorkspace.Value;

        // Already open: bring it forward rather than building a second one. The
        // sessions in it are the point — rebuilding them from the definition is
        // what "switching killed my processes" was.
        if (FindOpenWorkspace(workspace.Key) is { } alreadyOpen)
        {
            // Timed in pieces because the whole of it runs on the thread that
            // draws, and from outside a slow switch is just a frozen window.
            // The autosave flush writes the workspace being left; reactivating
            // announces the accent, which republishes every appearance token;
            // the snapshot serialises the workspace that is now in front.
            var clock = Stopwatch.StartNew();
            await FlushWorkspaceAutoSaveAsync();
            var flushed = clock.ElapsedMilliseconds;
            ReactivateRuntimeWorkspace(alreadyOpen);
            var reactivated = clock.ElapsedMilliseconds;
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            clock.Stop();
            ReportSwitchPhases(
                workspace.Name,
                flushed,
                reactivated - flushed,
                clock.ElapsedMilliseconds - reactivated);
            return true;
        }

        await FlushWorkspaceAutoSaveAsync();
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            workspace.Name,
            WorkspaceTints.Of(workspace),
            ResolveWorkspaceConnections(workspace),
            RuntimeAgentPolicyProvenance.Default.WithOverride(
                workspace.AgentPolicyOverride,
                workspace.Key,
                storedWorkspace.Revision),
            workspace.TerminalMultiplexingOverride);
        if (runtime.TerminalMultiplexingMode is { } workspaceMultiplexingOverride)
        {
            _workspaceTerminalMultiplexingModes[runtime.Id] = workspaceMultiplexingOverride;
        }
        try
        {
            foreach (var entry in workspace.Entries)
            {
                switch (entry)
                {
                    case WorkspaceEntry.ConnectionReference connectionReference:
                        var connection = FindConnection(connectionReference.ConnectionId);
                        if (connection is not null)
                        {
                            runtime.Tabs.Add(CreateConnectionTab(
                                runtime.Id,
                                connection,
                                connectionReference.Alias,
                                runtime.AgentPolicy));
                        }

                        break;
                    case WorkspaceEntry.ScreenReference screenReference:
                        var storedScreen = _catalog.Snapshot.Screens
                            .SingleOrDefault(
                                item => item.Value.Id == screenReference.ScreenId);
                        if (storedScreen is not null)
                        {
                            var screen = storedScreen.Value;
                            runtime.Tabs.Add(CreateRuntimeTab(
                                runtime.Id,
                                screenReference.Alias ?? screen.Name,
                                "Saved screen",
                                screen.LayoutId,
                                screen.Panels,
                                screen.Key,
                                screen.Name,
                                runtime.AgentPolicy.WithOverride(
                                    screen.AgentPolicyOverride,
                                    screen.Key,
                                    storedScreen.Revision)));
                        }

                        break;
                    case WorkspaceEntry.Tab tab:
                        runtime.Tabs.Add(CreateRuntimeTab(
                            runtime.Id,
                            tab.Name,
                            "Workspace tab",
                            tab.LayoutId,
                            tab.Panels,
                            workspace.Key,
                            workspace.Name,
                            runtime.AgentPolicy));
                        break;
                }
            }

            if (runtime.Tabs.Count == 0)
            {
                var firstConnection = ResolveWorkspaceConnectionDefinitions(workspace).FirstOrDefault()
                    ?? _catalog.Snapshot.Connections.FirstOrDefault()?.Value;
                if (firstConnection is not null)
                {
                    runtime.Tabs.Add(CreateConnectionTab(
                        runtime.Id,
                        firstConnection,
                        agentPolicy: runtime.AgentPolicy));
                }
            }

            if (runtime.Tabs.Count == 0)
            {
                runtime.Tabs.Add(CreateLauncherTab());
            }

            runtime.ActiveTab = runtime.Tabs[0];
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, workspace.Key, workspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenConnectionAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return false;
        }

        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            connection.Name,
            ThemePreference.BronzeFallback.ToString(),
            Connections.Where(item => item.Id == connection.Id).ToArray());
        try
        {
            runtime.Tabs.Add(CreateConnectionTab(runtime.Id, connection));
            runtime.ActiveTab = runtime.Tabs[0];
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, connection.Key, connection.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenScreenAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var storedScreen = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (storedScreen is null)
        {
            SetError("That saved screen no longer exists.");
            return false;
        }

        var screen = storedScreen.Value;
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            screen.Name,
            ThemePreference.BronzeFallback.ToString(),
            Connections.ToArray());
        try
        {
            runtime.Tabs.Add(CreateRuntimeTab(
                runtime.Id,
                screen.Name,
                "Saved screen",
                screen.LayoutId,
                screen.Panels,
                screen.Key,
                screen.Name,
                runtime.AgentPolicy.WithOverride(
                    screen.AgentPolicyOverride,
                    screen.Key,
                    storedScreen.Revision)));
            runtime.ActiveTab = runtime.Tabs[0];
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, screen.Key, screen.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public Task<bool> LaunchConnectionAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default) =>
        RuntimeWorkspace is null
            ? OpenConnectionAsync(connectionId, cancellationToken)
            : AddConnectionTabAsync(connectionId, cancellationToken);

    /// <summary>
    /// A saved screen brings its own tab. Asked for from a tab that is nothing
    /// but the launcher, it takes that tab over rather than opening beside it:
    /// the launcher tab was the question, and this is the answer.
    /// </summary>
    public Task<bool> LaunchScreenAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeWorkspace is null)
        {
            return OpenScreenAsync(screenId, cancellationToken);
        }

        return RuntimeWorkspace.ActiveTab is { } tab && IsLauncherTab(tab)
            ? ReplaceScreenTabAsync(tab, screenId, cancellationToken)
            : AddScreenTabAsync(screenId, cancellationToken);
    }

    /// <summary>Opens a saved file provider in a tab, like a terminal connection.</summary>
    public async Task<bool> LaunchFileProviderAsync(
        FileProviderProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var stored = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            SetError("That file connection no longer exists.");
            return false;
        }

        var profile = stored.Value;
        if (RuntimeWorkspace is not null)
        {
            return await AddFileProviderTabAsync(
                profile.Id,
                PanelKind.FileViewer,
                cancellationToken);
        }

        var runtimeWorkspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            profile.Name,
            ThemePreference.BronzeFallback.ToString(),
            []);
        try
        {
            var tab = CreateFileProviderTab(runtimeWorkspace.Id, profile);
            if (tab is null)
            {
                return false;
            }

            runtimeWorkspace.Tabs.Add(tab);
            runtimeWorkspace.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtimeWorkspace, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtimeWorkspace, null, runtimeWorkspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtimeWorkspace);
        }
    }

    /// <summary>Opens a saved database connection in a tab.</summary>
    public async Task<bool> LaunchSavedDatabaseAsync(
        DatabaseConnectionProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_databasePanelClient is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        var profile = FindDatabaseConnection(profileId);
        if (profile is null)
        {
            SetError("That database connection no longer exists.");
            return false;
        }

        if (RuntimeWorkspace is { } workspace)
        {
            return await AppendRuntimeTabAsync(
                workspace,
                runtime => CreateSavedDatabaseTab(profile),
                "database connection tab creation",
                cancellationToken);
        }

        var runtimeWorkspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            profile.Name,
            ThemePreference.BronzeFallback.ToString(),
            []);
        try
        {
            var tab = CreateSavedDatabaseTab(profile);
            runtimeWorkspace.Tabs.Add(tab);
            runtimeWorkspace.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtimeWorkspace, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtimeWorkspace, null, runtimeWorkspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtimeWorkspace);
        }
    }

    private RuntimeTabViewModel CreateSavedDatabaseTab(DatabaseConnectionProfile profile)
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            profile.Name,
            "Database");
        var panel = CreateDatabasePanelFromTarget(
            PanelInstanceId.New(),
            profile.Name,
            SavedDatabaseTargetPrefix + profile.Id.Value,
            recoveredTunnel: null);
        AddPanelOrDispose(tab, panel);
        return tab;
    }

    public async Task<bool> OpenLocalMonitorWorkspaceAsync(
        PanelKind kind,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Local system",
            ThemePreference.BronzeFallback.ToString(),
            []);
        try
        {
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                kind == PanelKind.Statistics ? "Statistics" : "Processes",
                "Local host");
            var panel = CreateMonitorPanel(
                runtime.Id,
                tab.Id,
                PanelInstanceId.New(),
                kind == PanelKind.Statistics ? "Statistics" : "Process Monitor",
                kind);
            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenLocalDatabaseWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_databasePanelClient is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Database",
            ThemePreference.BronzeFallback.ToString(),
            []);
        try
        {
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                "Database",
                "Local");
            var panel = CreateDatabasePanel(PanelInstanceId.New(), "Database");
            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenLocalBrowserWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Browser",
            ThemePreference.BronzeFallback.ToString(),
            []);
        try
        {
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                "Browser",
                "Local");
            var panel = CreateBrowserPanel(
                runtime.Id,
                tab.Id,
                PanelInstanceId.New(),
                "Browser",
                BrowserAddress.Blank);
            if (panel is not BrowserRuntimePanelViewModel)
            {
                SetError("The embedded browser could not be initialized.");
                panel.Dispose();
                return false;
            }

            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> ActivateTabAsync(
        TabInstanceId tabId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null || workspace.Tabs.All(tab => tab.Id != tabId))
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var request = new ActivateWorkspaceTabRequest(workspace.Id, tabId);
            var idempotencyKey = IdempotencyKey.New();
            for (var attempt = 0; attempt < WorkspaceMutationAttemptCount; attempt++)
            {
                var result = await SessionClient.ActivateWorkspaceTabAsync(
                    request,
                    OperationContext.ForHuman(
                        ClientId,
                        workspace.HostRevision,
                        idempotencyKey),
                    linkedCancellation.Token);
                if (await TryRefreshRevisionConflictAsync(
                    workspace,
                    result,
                    attempt,
                    linkedCancellation.Token))
                {
                    continue;
                }

                return TryApplyRuntimeWorkspaceResult(
                    workspace,
                    result,
                    "tab activation",
                    projection => projection.ActiveTabId == tabId);
            }

            return false;
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> ActivatePanelAsync(
        PanelInstanceId panelId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(item =>
            item.Panels.Any(panel => panel.Id == panelId));
        if (workspace is null || tab is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var request = new ActivateWorkspacePanelRequest(
                workspace.Id,
                tab.Id,
                panelId);
            var idempotencyKey = IdempotencyKey.New();
            for (var attempt = 0; attempt < WorkspaceMutationAttemptCount; attempt++)
            {
                var result = await SessionClient.ActivateWorkspacePanelAsync(
                    request,
                    OperationContext.ForHuman(
                        ClientId,
                        workspace.HostRevision,
                        idempotencyKey),
                    linkedCancellation.Token);
                if (await TryRefreshRevisionConflictAsync(
                    workspace,
                    result,
                    attempt,
                    linkedCancellation.Token))
                {
                    continue;
                }

                return TryApplyRuntimeWorkspaceResult(
                    workspace,
                    result,
                    "panel activation",
                    projection =>
                        projection.ActiveTabId == tab.Id
                        && projection.Tabs.SingleOrDefault(
                                candidate => candidate.Id == tab.Id)
                            ?.ActivePanelId == panelId);
            }

            return false;
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> LoadSessionRestorePreferenceAsync(
        CancellationToken cancellationToken = default)
    {
        if (_sessionRestoreCoordinator is null)
        {
            return true;
        }

        var result = await _sessionRestoreCoordinator.ReadPreferenceAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            SetError("The session restore preference could not be loaded.");
            return false;
        }

        RestoreSessionsOnStart = result.Value;
        _sessionRestorePreferenceLoaded = true;
        OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        return true;
    }

    public async Task<bool> SetRestoreSessionsOnStartAsync(
        bool restoreSessionsOnStart,
        CancellationToken cancellationToken = default)
    {
        if (_sessionRestoreCoordinator is null)
        {
            RestoreSessionsOnStart = restoreSessionsOnStart;
            return true;
        }

        if (!_sessionRestorePreferenceLoaded || _sessionRestorePreferenceSaving)
        {
            return false;
        }

        _sessionRestorePreferenceSaving = true;
        OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        try
        {
            var result = await _sessionRestoreCoordinator.WritePreferenceAsync(
                restoreSessionsOnStart,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetError("The session restore preference could not be saved.");
                OnPropertyChanged(nameof(RestoreSessionsOnStart));
                return false;
            }

            RestoreSessionsOnStart = restoreSessionsOnStart;
            return true;
        }
        finally
        {
            _sessionRestorePreferenceSaving = false;
            OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        }
    }

    public async Task<bool> LoadTerminalMultiplexingAsync(
        CancellationToken cancellationToken = default)
    {
        if (_terminalMultiplexerCoordinator is null)
        {
            _terminalMultiplexingPreferenceLoaded = true;
            return true;
        }

        var preference = await _terminalMultiplexerCoordinator.ReadPreferenceAsync(cancellationToken);
        var leases = await _terminalMultiplexerCoordinator.ListAsync(cancellationToken);
        if (!preference.IsSuccess || !leases.IsSuccess)
        {
            SetError("Terminal continuity settings could not be loaded.");
            return false;
        }

        _terminalMultiplexingMode = preference.Value;
        _terminalMultiplexingPreferenceLoaded = true;
        OnPropertyChanged(nameof(UseTerminalMultiplexingForSshTerminals));
        OnPropertyChanged(nameof(CanChangeTerminalMultiplexing));
        RefreshManagedRemoteSessions(leases.Value!);
        return true;
    }

    public async Task<bool> SetUseTerminalMultiplexingForSshTerminalsAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var mode = enabled
            ? TerminalMultiplexingMode.Automatic
            : TerminalMultiplexingMode.Disabled;
        if (_terminalMultiplexerCoordinator is null)
        {
            _terminalMultiplexingMode = mode;
            OnPropertyChanged(nameof(UseTerminalMultiplexingForSshTerminals));
            return true;
        }

        if (!_terminalMultiplexingPreferenceLoaded || _terminalMultiplexingPreferenceSaving)
        {
            return false;
        }

        _terminalMultiplexingPreferenceSaving = true;
        OnPropertyChanged(nameof(CanChangeTerminalMultiplexing));
        try
        {
            var result = await _terminalMultiplexerCoordinator.WritePreferenceAsync(
                mode,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetError("Terminal continuity settings could not be saved.");
                OnPropertyChanged(nameof(UseTerminalMultiplexingForSshTerminals));
                return false;
            }

            _terminalMultiplexingMode = mode;
            OnPropertyChanged(nameof(UseTerminalMultiplexingForSshTerminals));
            return true;
        }
        finally
        {
            _terminalMultiplexingPreferenceSaving = false;
            OnPropertyChanged(nameof(CanChangeTerminalMultiplexing));
        }
    }

    public async Task TerminateManagedRemoteSessionAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_terminalMultiplexerCoordinator is null
            || FindConnection(item.Lease.ConnectionId) is not { } connection)
        {
            SetError("The connection for this managed remote session is unavailable.");
            return;
        }

        var result = await _terminalMultiplexerCoordinator.TerminateAsync(
            connection,
            item.Lease.Session,
            cancellationToken);
        if (!result.Terminated)
        {
            SetError(result.Detail);
        }

        await RefreshManagedRemoteSessionsAsync(cancellationToken);
    }

    public async Task ForgetManagedRemoteSessionAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_terminalMultiplexerCoordinator is null)
        {
            return;
        }

        _ = await _terminalMultiplexerCoordinator.ForgetAsync(item.Lease, cancellationToken);
        await RefreshManagedRemoteSessionsAsync(cancellationToken);
    }

    private async Task RefreshManagedRemoteSessionsAsync(CancellationToken cancellationToken)
    {
        if (_terminalMultiplexerCoordinator is null)
        {
            return;
        }

        var result = await _terminalMultiplexerCoordinator.ListAsync(cancellationToken);
        if (result.IsSuccess)
        {
            RefreshManagedRemoteSessions(result.Value!);
        }
    }

    private void RefreshManagedRemoteSessions(
        IReadOnlyList<TerminalMultiplexerLease> leases)
    {
        ManagedRemoteSessions.Clear();
        foreach (var lease in leases)
        {
            ManagedRemoteSessions.Add(new ManagedRemoteSessionViewModel(
                lease,
                FindConnection(lease.ConnectionId)?.Name ?? lease.ConnectionId.Value));
        }

        OnPropertyChanged(nameof(HasManagedRemoteSessions));
    }

    private async void OnTerminalMultiplexerLeasesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_terminalMultiplexerCoordinator is null || _disposed)
        {
            return;
        }

        var result = await _terminalMultiplexerCoordinator.ListAsync(CancellationToken.None);
        if (!result.IsSuccess || _disposed)
        {
            return;
        }

        await _uiThreadDispatcher.InvokeAsync(
            () => RefreshManagedRemoteSessions(result.Value!),
            CancellationToken.None);
    }

    /// <summary>
    /// Opens Main on its launcher when the window has come up with nothing in it.
    ///
    /// Main always exists, so there is always somewhere for the launcher to
    /// live. It defers to everything that has a better claim on the window: a
    /// restored session or an overlay. First-run onboarding is part of the
    /// launcher, so it needs this workspace rather than blocking it.
    /// </summary>
    public async Task<bool> OpenDefaultLauncherIfIdleAsync(
        CancellationToken cancellationToken = default)
    {
        if (RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        var main = Workspaces.FirstOrDefault(item => string.Equals(
            item.Id.Value,
            WorkspaceDefinition.DefaultWorkspaceId,
            StringComparison.Ordinal));
        if (main is null
            || !await OpenWorkspaceAsync(main.Id, cancellationToken))
        {
            return false;
        }

        if (RuntimeWorkspace?.ActiveTab is { } activeTab
            && IsLauncherTab(activeTab))
        {
            return true;
        }

        return await AddLauncherTabAsync(cancellationToken);
    }

    public async Task<bool> RestoreSessionOnStartupAsync(
        CancellationToken cancellationToken = default)
    {
        _ = await LoadTerminalMultiplexingAsync(cancellationToken);
        if (_sessionRestoreCoordinator is null)
        {
            Console.Error.WriteLine(
                "[ghostshell:recovery] Startup session restore is unavailable.");
            return false;
        }

        if (!await LoadSessionRestorePreferenceAsync(cancellationToken))
        {
            Console.Error.WriteLine(
                "[ghostshell:recovery] Startup session restore preference could not be loaded.");
            return false;
        }

        if (!RestoreSessionsOnStart
            || RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        var result = await _sessionRestoreCoordinator.LoadLatestSessionAsync(
            cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(
                $"[ghostshell:recovery] Previous session lookup failed: "
                + $"{result.Error!.Code}: {result.Error.Message}");
            SetError("The previous session could not be loaded.");
            return false;
        }

        if (RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        return await RestoreRuntimeSnapshotsAsync(result.Value!, cancellationToken);
    }

    /// <summary>
    /// Opens the window on stored runtime state. The one way in: how the run
    /// that wrote these snapshots ended is not a question anyone asks here,
    /// because the answer never changed what gets opened.
    /// </summary>
    public async Task<bool> RestoreRuntimeSnapshotsAsync(
        IReadOnlyList<RuntimeRecoverySnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var snapshot = snapshots
            .Where(item => item.Key == RuntimeWorkspaceRecoveryCodec.SnapshotKey)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (snapshot is null)
        {
            return false;
        }

        if (!RuntimeWorkspaceRecoveryCodec.TryDeserialize(
                snapshot,
                out var payload,
                out var error))
        {
            Console.Error.WriteLine(
                $"[ghostshell:recovery] Previous session payload was rejected: {error}");
            SetError(error ?? "Runtime recovery state could not be read.");
            return false;
        }

        if (payload!.Workspace is null)
        {
            RuntimeWorkspace = null;
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        RuntimeWorkspaceViewModel? runtime = null;
        try
        {
            runtime = RestoreWorkspace(payload.Workspace);
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                Console.Error.WriteLine(
                    "[ghostshell:recovery] The restored workspace was rejected by the session host.");
                return false;
            }

            // Through ActivateRuntimeWorkspace, not around it. Restoring by
            // hand left the workspace out of the open set and with no record of
            // which definition it came from, so clicking its own rail tile
            // built a second copy and the terminals it was restored with were
            // replaced by fresh shells.
            if (payload.Workspace.HistorySource?.ToHistorySource() is { } restoredSource)
            {
                ActivateRuntimeWorkspace(
                    runtime,
                    restoredSource.SourceDefinition,
                    restoredSource.DurableTitle);
            }
            else
            {
                // Restored from a snapshot written before workspaces recorded
                // where they came from. It cannot be found again by definition,
                // but it must still be in the open set.
                ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            }

            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(
                $"[ghostshell:recovery] Runtime recovery target was invalid: {exception}");
            SetError("Runtime recovery state contains an invalid target.");
            return false;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(
                $"[ghostshell:recovery] Runtime recovery could not be applied: {exception}");
            SetError("Runtime recovery state could not be applied.");
            return false;
        }
        finally
        {
            if (runtime is not null)
            {
                DisposeRuntimeWorkspaceUnlessOwned(runtime);
            }
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> CreateWorkspaceAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ClearError();
        var definition = new WorkspaceDefinition(
            WorkspaceId.New(),
            WorkspaceDefinition.CurrentSchemaVersion,
            RequireName(name, "Workspace"),
            "A GhostSHELL workspace.",
            ThemePreference.BronzeFallback.ToString(),
            []);
        var result = await _catalog.SaveWorkspaceAsync(definition, null, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public ConnectionEditorViewModel CreateConnectionEditor(ConnectionId? connectionId = null)
    {
        if (connectionId is null)
        {
            return new ConnectionEditorViewModel(
                _connectionRuntime,
                securityRuntime: _connectionSecurityRuntime);
        }

        var stored = _catalog.Snapshot.Connections
            .SingleOrDefault(item => item.Value.Id == connectionId.Value);
        if (stored is null)
        {
            throw new InvalidOperationException("That connection no longer exists.");
        }

        return new ConnectionEditorViewModel(
            _connectionRuntime,
            stored.Value,
            stored.Revision,
            _connectionSecurityRuntime);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await _catalog.SaveConnectionAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public FileProviderProfileEditorViewModel CreateFileProviderEditor(
        FileProviderProfileId? profileId = null)
    {
        var runtime = _fileProviderRuntime
            ?? throw new InvalidOperationException("The file-provider runtime is unavailable.");
        var connections = _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .ToArray();
        if (profileId is null)
        {
            return new FileProviderProfileEditorViewModel(
                runtime,
                connections,
                Secrets.ToArray());
        }

        var stored = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value);
        if (stored is null)
        {
            throw new InvalidOperationException("That file-provider profile no longer exists.");
        }

        return new FileProviderProfileEditorViewModel(
            runtime,
            connections,
            Secrets.ToArray(),
            stored.Value,
            stored.Revision);
    }

    /// <summary>
    /// Builds the single editor that covers every connection family. A locked
    /// family (used when editing an existing definition) restricts the type
    /// selector to that family; otherwise every available family contributes.
    /// </summary>
    public UnifiedConnectionEditorViewModel CreateUnifiedConnectionEditor(
        SavedConnectionFamily? lockedFamily = null,
        ConnectionId? terminalConnectionId = null,
        FileProviderProfileId? fileProfileId = null,
        DatabaseConnectionProfileId? databaseProfileId = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = CreateConnectionEditor(terminalConnectionId);
        FileProviderProfileEditorViewModel? files = null;
        if (_fileProviderRuntime is not null
            && lockedFamily is null or SavedConnectionFamily.Files)
        {
            files = CreateFileProviderEditor(fileProfileId);
        }

        DatabaseConnectionEditorViewModel? database = null;
        if (_databasePanelClient is not null
            && lockedFamily is null or SavedConnectionFamily.Database)
        {
            DatabaseConnectionProfile? existing = null;
            if (databaseProfileId is { } databaseId)
            {
                existing = FindDatabaseConnection(databaseId)
                    ?? throw new InvalidOperationException(
                        "That database connection no longer exists.");
            }

            database = new DatabaseConnectionEditorViewModel(
                _databasePanelClient,
                _catalog.Snapshot.Connections.Select(item => item.Value).ToArray(),
                existing,
                existing?.PasswordSecret is { } storedSecret
                    ? token => ResolveDatabasePasswordAsync(storedSecret, token)
                    : null);
        }

        return new UnifiedConnectionEditorViewModel(
            terminal,
            files,
            database,
            lockedFamily,
            initialFamily);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
        SaveFileProviderProfileAsync(
            FileProviderProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await _catalog.SaveFileProviderProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public AiProviderProfileEditorViewModel CreateAiProviderEditor(
        AiProviderProfileId? profileId = null)
    {
        var runtime = _aiProviderRuntime
            ?? throw new InvalidOperationException("The AI-provider runtime is unavailable.");
        if (profileId is null)
        {
            return new AiProviderProfileEditorViewModel(
                runtime,
                Secrets.ToArray(),
                suggestedOrder: NextAiProviderOrder(_catalog.Snapshot));
        }

        var stored = _catalog.Snapshot.AiProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value);
        if (stored is null)
        {
            throw new InvalidOperationException("That AI-provider profile no longer exists.");
        }

        return new AiProviderProfileEditorViewModel(
            runtime,
            Secrets.ToArray(),
            stored.Value,
            stored.Revision);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
        SaveAiProviderProfileAsync(
            AiProviderProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await _catalog.SaveAiProviderProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public McpServerProfileEditorViewModel CreateMcpServerEditor(
        McpServerProfileId? profileId = null)
    {
        if (profileId is null)
        {
            return new McpServerProfileEditorViewModel(
                secrets: Secrets.ToArray());
        }

        var stored = _catalog.Snapshot.McpServerProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value);
        if (stored is null)
        {
            throw new InvalidOperationException("That MCP-server profile no longer exists.");
        }

        return new McpServerProfileEditorViewModel(
            stored.Value,
            stored.Revision,
            Secrets.ToArray());
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
        SaveMcpServerProfileAsync(
            McpServerProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        if (!request.IsAuthorizedForSave)
        {
            return Fail<StoredDefinition<McpServerProfile>>(
                "Confirm the trusted MCP launch details before saving this profile.");
        }

        var result = await _catalog.SaveMcpServerProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask TestMcpServerAsync(
        McpServerProfileItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_mcpServerDiagnostics is null || !item.CanTest)
        {
            return;
        }

        // The row is updated in place to preserve keyboard focus, so freeze the
        // tested identity before awaiting a catalog or diagnostics change.
        var profileId = item.Id;
        var revision = item.Revision;
        var current = _catalog.Snapshot.McpServerProfiles
            .SingleOrDefault(stored =>
                stored.Value.Id == profileId);
        if (current is null || current.Revision != revision)
        {
            SetMcpServerTest(
                profileId,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test unavailable",
                    "The MCP server changed before the test could start."));
            RefreshMcpServerDefinitions(_catalog.Snapshot);
            return;
        }

        if (!TryBeginMcpServerTest(profileId, revision))
        {
            return;
        }

        RefreshMcpServerDefinitions(_catalog.Snapshot);
        try
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var result = await _mcpServerDiagnostics.TestAsync(
                    new McpServerTestRequest(
                        profileId,
                        revision),
                    OperationContext.ForHuman(
                        ClientId,
                        expectedRevision: revision,
                        deadlineUtc: now + TimeSpan.FromSeconds(30)),
                    cancellationToken)
                .ConfigureAwait(true);
            switch (result)
            {
                case McpServerTestResult.Success success:
                    CompleteMcpServerTest(
                        profileId,
                        revision,
                        CreateMcpServerTestSuccess(
                            current,
                            success.Report));
                    break;
                case McpServerTestResult.Failure failure:
                    CompleteMcpServerTest(
                        profileId,
                        revision,
                        new McpServerTestPresentation(
                            revision,
                            McpServerTestPresentationState.Failed,
                            "Test failed",
                            failure.Error.Message));
                    break;
                default:
                    throw new InvalidOperationException(
                        "The MCP server test result is invalid.");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CompleteMcpServerTest(
                profileId,
                revision,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test cancelled",
                    "The bounded MCP server test was cancelled."));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            CompleteMcpServerTest(
                profileId,
                revision,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test failed",
                    "The MCP diagnostics boundary could not complete the test."));
        }

        if (!_disposed)
        {
            RefreshMcpServerDefinitions(_catalog.Snapshot);
        }
    }

    public async ValueTask<bool> CreateConnectionSecretAsync(
        ConnectionId connectionId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (FindConnection(connectionId) is null)
        {
            SetError("Choose an existing connection for this credential.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(value))
        {
            SetError("Credential label and value are required.");
            return false;
        }

        var scope = new SecretScope(SecretScopeKind.Connection, connectionId.Value);
        var purpose = new SecretUsePurpose(SecretUseKind.UserManagement, connectionId.Value);
        var bytes = Encoding.UTF8.GetBytes(value);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var result = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                SecretRef.New(),
                label.Trim(),
                kind,
                scope,
                purpose),
            material,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        await RefreshSecretsAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> CreateFileProviderSecretAsync(
        FileProviderProfileId profileId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (_catalog.Snapshot.FileProviderProfiles.All(item => item.Value.Id != profileId))
        {
            SetError("Choose an existing file-provider profile for this credential.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(value))
        {
            SetError("Credential label and value are required.");
            return false;
        }

        var scope = new SecretScope(SecretScopeKind.FileProvider, profileId.Value);
        var purpose = new SecretUsePurpose(SecretUseKind.UserManagement, profileId.Value);
        var bytes = Encoding.UTF8.GetBytes(value);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var result = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                SecretRef.New(),
                label.Trim(),
                kind,
                scope,
                purpose),
            material,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        await RefreshSecretsAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> CreateAiProviderSecretAsync(
        AiProviderProfileId profileId,
        string label,
        string value,
        CancellationToken cancellationToken)
    {
        ClearError();
        var profile = _catalog.Snapshot.AiProviderProfiles
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            SetError("Choose an existing AI-provider profile for this credential.");
            return false;
        }

        if (profile.Authentication is not AiProviderAuthentication.ApiKey apiKey)
        {
            SetError("This provider is configured for local unauthenticated access.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(value))
        {
            SetError("Credential label and value are required.");
            return false;
        }

        var scope = new SecretScope(SecretScopeKind.AiProvider, profileId.Value);
        var purpose = new SecretUsePurpose(SecretUseKind.UserManagement, profileId.Value);
        var bytes = Encoding.UTF8.GetBytes(value);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var result = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                apiKey.Secret,
                label.Trim(),
                SecretKind.ApiKey,
                scope,
                purpose),
            material,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        await RefreshSecretsAsync(cancellationToken);
        if (_aiProviderRuntime is not null)
        {
            await _aiProviderRuntime.ReloadAsync(cancellationToken);
        }

        return true;
    }

    public async ValueTask<bool> CreateMcpServerSecretAsync(
        McpEnvironmentSecretTargetViewModel target,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ClearError();
        var profile = _catalog.Snapshot.McpServerProfiles
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == target.ProfileId);
        var bindingStillExists = profile?.Environment.Any(binding =>
            string.Equals(binding.Name, target.VariableName, StringComparison.Ordinal)
            && binding.Reference == target.Reference) == true;
        if (!bindingStillExists)
        {
            SetError("That MCP environment binding changed. Reopen the server settings.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(value))
        {
            SetError("Credential label and value are required.");
            return false;
        }

        var scope = new SecretScope(SecretScopeKind.McpServer, target.ProfileId.Value);
        var bytes = Encoding.UTF8.GetBytes(value);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var result = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                target.Reference,
                label.Trim(),
                kind,
                scope,
                new SecretUsePurpose(
                    SecretUseKind.UserManagement,
                    target.ProfileId.Value)),
            material,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        await RefreshSecretsAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> DeleteSecretAsync(
        SecretMetadataViewModel secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ClearError();
        var connectionDependents = _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .Where(connection => UsesSecret(connection, secret.Reference))
            .Select(connection => $"connection {connection.Name}");
        var providerDependents = _catalog.Snapshot.FileProviderProfiles
            .Select(item => item.Value)
            .Where(profile => UsesSecret(profile, secret.Reference))
            .Select(profile => $"file provider {profile.Name}");
        var aiProviderDependents = _catalog.Snapshot.AiProviderProfiles
            .Select(item => item.Value)
            .Where(profile => UsesSecret(profile, secret.Reference))
            .Select(profile => $"AI provider {profile.Name}");
        var mcpServerDependents = _catalog.Snapshot.McpServerProfiles
            .Select(item => item.Value)
            .Where(profile => UsesSecret(profile, secret.Reference))
            .Select(profile => $"MCP server {profile.Name}");
        var dependents = connectionDependents
            .Concat(providerDependents)
            .Concat(aiProviderDependents)
            .Concat(mcpServerDependents)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dependents.Length > 0)
        {
            SetError($"This credential is still referenced by: {string.Join(", ", dependents)}. Replace the reference before deleting it.");
            return false;
        }

        var targetId = secret.SecretScope.Kind == SecretScopeKind.Global
            ? SecretUsePurpose.GlobalTargetId
            : secret.SecretScope.OwnerId!;
        var result = await _secretVault.DeleteAsync(
            new DeleteSecretRequest(
                secret.Reference,
                secret.SecretScope,
                new SecretUsePurpose(SecretUseKind.UserManagement, targetId)),
            cancellationToken);
        if (result is SecretVaultResult<Unit>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        if (secret.SecretScope.Kind == SecretScopeKind.McpServer)
        {
            InvalidateMcpServerTests(secret.Reference);
            if (_mcpCredentialSessionInvalidator is not null)
            {
                await _mcpCredentialSessionInvalidator
                    .InvalidateAsync(secret.Reference);
            }
        }

        await RefreshSecretsAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> RelabelSecretAsync(
        SecretMetadataViewModel secret,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ClearError();
        SecretVaultResult<SecretMetadata> result;
        try
        {
            result = await _secretVault.RelabelAsync(
                new RelabelSecretRequest(
                    secret.Reference,
                    secret.SecretScope,
                    label,
                    ManagementPurpose(secret)),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            SetError(exception.Message);
            return false;
        }

        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        await RefreshSecretsAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> ReplaceSecretAsync(
        SecretMetadataViewModel secret,
        SecretMaterial replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(replacement);
        ClearError();
        var result = await _secretVault.ReplaceAsync(
            new ReplaceSecretRequest(
                secret.Reference,
                secret.SecretScope,
                ManagementPurpose(secret)),
            replacement,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        if (secret.SecretScope.Kind == SecretScopeKind.McpServer)
        {
            InvalidateMcpServerTests(secret.Reference);
            if (_mcpCredentialSessionInvalidator is not null)
            {
                await _mcpCredentialSessionInvalidator
                    .InvalidateAsync(secret.Reference);
            }
        }

        await RefreshSecretsAsync(cancellationToken);
        if (secret.SecretScope.Kind == SecretScopeKind.FileProvider
            && _fileProviderRuntime is not null)
        {
            await _fileProviderRuntime.ReloadAsync(cancellationToken);
        }
        else if (secret.SecretScope.Kind == SecretScopeKind.AiProvider
            && _aiProviderRuntime is not null)
        {
            await _aiProviderRuntime.ReloadAsync(cancellationToken);
        }

        return true;
    }

    public async ValueTask<bool> CancelFileTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await ResolveFileTransferQueue(id).CancelAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Error!.Message);
            return false;
        }

        RefreshFileTransfers();
        return true;
    }

    public async ValueTask<bool> QueueFileTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await _fileTransferQueue.EnqueueAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Error!.Message);
            return false;
        }

        RefreshFileTransfers();
        return true;
    }

    public async ValueTask<bool> RetryFileTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await ResolveFileTransferQueue(id).RetryAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Error!.Message);
            return false;
        }

        RefreshFileTransfers();
        return true;
    }

    private static bool UsesSecret(ConnectionProfile connection, SecretRef reference) =>
        (connection.Authentication switch
        {
            ConnectionAuthentication.Password password => password.PasswordSecret == reference,
            ConnectionAuthentication.PrivateKey privateKey =>
                privateKey.PrivateKeySecret == reference
                || privateKey.PassphraseSecret == reference,
            _ => false,
        })
        || connection.Startup.Environment.Any(variable =>
            variable.Value is ConnectionEnvironmentValue.Secret secret
            && secret.Reference == reference);

    private static bool UsesSecret(FileProviderProfile profile, SecretRef reference) =>
        profile.Configuration switch
        {
            FileProviderConfiguration.S3 value => value.CredentialsSecret == reference,
            FileProviderConfiguration.Ftp value => value.PasswordSecret == reference,
            FileProviderConfiguration.Smb value => value.PasswordSecret == reference,
            FileProviderConfiguration.WebDav value => value.PasswordSecret == reference,
            _ => false,
        };

    private static bool UsesSecret(AiProviderProfile profile, SecretRef reference) =>
        profile.Authentication is AiProviderAuthentication.ApiKey apiKey
        && apiKey.Secret == reference;

    private static bool UsesSecret(McpServerProfile profile, SecretRef reference) =>
        profile.Environment.Any(binding => binding.Reference == reference);

    private static int NextAiProviderOrder(DefinitionCatalogSnapshot snapshot)
    {
        var used = snapshot.AiProviderProfiles
            .Select(item => item.Value.Order)
            .ToHashSet();
        for (var order = 0; order <= AiProviderProfile.MaximumOrder; order++)
        {
            if (!used.Contains(order))
            {
                return order;
            }
        }

        throw new InvalidOperationException(
            "Every available AI-provider fallback position is already in use.");
    }

    public async Task RefreshSecretsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var availability = _secretVault.Availability;
            SecretVaultStatus = availability.Message;
            var result = await _secretVault.ListMetadataAsync(
                new ListSecretMetadataRequest(null, SecretUsePurpose.ManageAll()),
                cancellationToken);
            if (result is SecretVaultResult<IReadOnlyList<SecretMetadata>>.Failure failure)
            {
                SecretVaultStatus = failure.Error.Message;
                return;
            }

            var metadata = ((SecretVaultResult<IReadOnlyList<SecretMetadata>>.Success)result).Value;
            var connections = _catalog.Snapshot.Connections
                .Select(item => item.Value)
                .ToArray();
            var fileProviders = _catalog.Snapshot.FileProviderProfiles
                .Select(item => item.Value)
                .ToArray();
            var aiProviders = _catalog.Snapshot.AiProviderProfiles
                .Select(item => item.Value)
                .ToArray();
            var mcpServers = _catalog.Snapshot.McpServerProfiles
                .Select(item => item.Value)
                .ToArray();
            Replace(Secrets, metadata
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    var connectionDependencies = connections
                        .Where(connection => UsesSecret(connection, item.Reference))
                        .Select(connection => $"connection {connection.Name}");
                    var providerDependencies = fileProviders
                        .Where(profile => UsesSecret(profile, item.Reference))
                        .Select(profile => $"file provider {profile.Name}");
                    var aiProviderDependencies = aiProviders
                        .Where(profile => UsesSecret(profile, item.Reference))
                        .Select(profile => $"AI provider {profile.Name}");
                    var mcpServerDependencies = mcpServers
                        .Where(profile => UsesSecret(profile, item.Reference))
                        .Select(profile => $"MCP server {profile.Name}");
                    var dependencies = connectionDependencies
                        .Concat(providerDependencies)
                        .Concat(aiProviderDependencies)
                        .Concat(mcpServerDependencies)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new SecretMetadataViewModel(
                        item.Reference,
                        item.Label,
                        item.Kind.ToString(),
                        item.Scope.Kind == SecretScopeKind.Global
                            ? "Global"
                            : $"{item.Scope.Kind} · {item.Scope.OwnerId}",
                        item.UpdatedAt.ToLocalTime().ToString("g"),
                        item.LastUsedAt?.ToLocalTime().ToString("g") ?? "Never",
                        item.Scope,
                        dependencies.Length == 0
                            ? "No saved definition dependencies"
                            : $"Used by: {string.Join(", ", dependencies)}",
                        dependencies.Length);
                }));
            OnPropertyChanged(nameof(HasNoSecrets));
            RefreshAiProviderDefinitions(_catalog.Snapshot);
            RefreshMcpServerDefinitions(_catalog.Snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SecretVaultStatus = "The operating-system credential vault could not be queried.";
            OnPropertyChanged(nameof(HasNoSecrets));
        }
    }

    private static SecretUsePurpose ManagementPurpose(SecretMetadataViewModel secret)
    {
        var targetId = secret.SecretScope.Kind == SecretScopeKind.Global
            ? SecretUsePurpose.GlobalTargetId
            : secret.SecretScope.OwnerId!;
        return new SecretUsePurpose(SecretUseKind.UserManagement, targetId);
    }

    public SavedScreenEditorViewModel CreateSavedScreenEditor(ScreenId screenId)
    {
        var stored = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (stored is null)
        {
            throw new InvalidOperationException("That saved screen no longer exists.");
        }

        return new SavedScreenEditorViewModel(
            stored.Value,
            stored.Revision,
            _catalog.Snapshot.Connections.Select(item => item.Value).ToArray(),
            _catalog.Snapshot.FileProviderProfiles.Select(item => item.Value).ToArray(),
            SelectableLayouts(),
            _aiProviderRuntime?.Profiles ?? []);
    }

    public SavedScreenEditorViewModel CreateNewSavedScreenEditor(string name)
    {
        return SavedScreenEditorViewModel.CreateNew(
            RequireName(name, "Saved screen"),
            SelectableLayouts(),
            _catalog.Snapshot.Connections.Select(item => item.Value).ToArray(),
            _catalog.Snapshot.FileProviderProfiles.Select(item => item.Value).ToArray(),
            _aiProviderRuntime?.Profiles ?? []);
    }

    /// <summary>
    /// The layouts a user may pick for new screens and tabs. Auto-saved layouts
    /// carry a live tab's captured geometry and stay out of every picker; the
    /// workspace editor still receives the full set so existing tab references
    /// resolve.
    /// </summary>
    private LayoutDefinition[] SelectableLayouts() => _catalog.Snapshot.Layouts
        .Select(item => item.Value)
        .Where(layout => !LayoutDefinition.IsAutoSaved(layout.Id))
        .ToArray();

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveSavedScreenAsync(
        SavedScreenEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await _catalog.SaveScreenAsync(
            request.Definition,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        if (TerminalSettingsEditor is null)
        {
            return Fail<StoredDefinition<TerminalProfile>>("No terminal profile is available to edit.");
        }

        TerminalProfileEditorSaveRequest request;
        try
        {
            request = TerminalSettingsEditor.CreateSaveRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Fail<StoredDefinition<TerminalProfile>>(exception.Message);
        }

        // Nothing to write is not a write. Saving unconditionally notified the
        // catalog, which rebuilt this editor, whose rebinding read as a fresh edit
        // and asked to save again — a loop that pinned a core at idle.
        if (ActiveTerminalProfile is { } stored
            && stored.RepresentsSameAs(request.Profile))
        {
            return DefinitionStoreResult<StoredDefinition<TerminalProfile>>.Success(
                new StoredDefinition<TerminalProfile>(
                    stored,
                    request.ExpectedRevision,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch));
        }

        var result = await _catalog.SaveTerminalProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        if (QuickTerminalSettingsEditor is null)
        {
            return Fail<StoredDefinition<QuickTerminalSettings>>(
                "Quick Terminal settings are unavailable.");
        }

        QuickTerminalSettingsSaveRequest request;
        try
        {
            request = QuickTerminalSettingsEditor.CreateSaveRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Fail<StoredDefinition<QuickTerminalSettings>>(exception.Message);
        }

        var result = await _catalog.SaveQuickTerminalSettingsAsync(
            request.Settings,
            request.ExpectedRevision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public void ApplyQuickTerminalRegistration(
        KeyStroke configuredGesture,
        KeyStroke? activeGesture,
        GlobalHotkeyRegistrationResult result) =>
        QuickTerminalSettingsEditor?.ApplyRegistration(
            configuredGesture,
            activeGesture,
            result);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> CreateLayoutAsync(
        string name,
        int rows,
        int columns,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (rows is < 1 or > 4 || columns is < 1 or > 4)
        {
            return Fail<StoredDefinition<LayoutDefinition>>("Layout rows and columns must be between one and four.");
        }

        var slots = new List<LayoutSlotDefinition>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new(
                    new LayoutSlotId($"slot-{row + 1}-{column + 1}"),
                    new LayoutGridBounds(column, row, 1, 1),
                    new LayoutMinimumSize(220, 140)));
            }
        }

        var geometry = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            RequireName(name, "Layout"),
            new LayoutGrid(columns, rows),
            slots);
        var definition = new LayoutDefinition(
            geometry.Id,
            geometry.SchemaVersion,
            geometry.Name,
            geometry.Grid,
            geometry.Slots,
            RuntimeDockLayoutController.SerializeDefinition(geometry));
        var result = await _catalog.SaveLayoutAsync(definition, null, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        AppearanceMode appearance,
        PlatformProfile platformProfile,
        AccentPreference accent,
        double? textScaleOverride,
        CancellationToken cancellationToken,
        ThemeChromePreference? chrome = null)
    {
        ClearError();
        var stored = _catalog.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id);
        // A caller that does not supply chrome settings keeps whatever is stored,
        // so saving from a surface that does not show them cannot silently reset
        // them to defaults.
        var existing = stored?.Value ?? ThemePreference.Default;
        var effective = chrome ?? ThemeChromePreference.From(existing);
        var updated = new ThemePreference(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            appearance,
            platformProfile,
            accent,
            textScaleOverride,
            effective.Density,
            effective.ShowTabBar,
            effective.ShowWorkspacesPanel,
            effective.TabStripPlacement,
            effective.WorkspacePanelPlacement,
            effective.IsTranslucent,
            effective.BackdropOpacityPercent,
            effective.HasGlassPanels,
            effective.OverridesBackdropOpacity);
        // A theme is all scalars, so record equality is enough to tell that this
        // would rewrite what is already stored.
        if (stored is not null && stored.Value == updated)
        {
            return DefinitionStoreResult<StoredDefinition<ThemePreference>>.Success(stored);
        }

        var result = await _catalog.SaveThemeAsync(
            updated,
            stored?.Revision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> SaveDefinitionEditAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        if (_editingDefinition is not { } key || _editingRevision is not { } revision)
        {
            return Fail<Unit>("Choose a workspace or saved screen to edit.");
        }

        if (key.Kind == WorkspaceDefinition.Kind)
        {
            var current = _catalog.Snapshot.Workspaces
                .Select(item => item.Value)
                .SingleOrDefault(item => item.Key == key);
            if (current is null)
            {
                return Fail<Unit>("That workspace no longer exists.");
            }

            var updated = new WorkspaceDefinition(
                current.Id,
                current.SchemaVersion,
                RequireName(EditorName, current.Name),
                EditorDescription,
                current.Accent,
                current.Entries,
                current.AgentPolicyOverride,
                current.Icon,
                current.AutoSave,
                current.Color,
                current.AgentPanelPinned,
                current.TerminalMultiplexingOverride);
            var saved = await _catalog.SaveWorkspaceAsync(updated, revision, cancellationToken);
            ApplyError(saved.Error);
            if (saved.IsSuccess)
            {
                CloseOverlay();
                return DefinitionStoreResult<Unit>.Success(Unit.Value);
            }

            return DefinitionStoreResult<Unit>.Failure(saved.Error!);
        }

        if (key.Kind == ScreenDefinition.Kind)
        {
            var current = _catalog.Snapshot.Screens
                .Select(item => item.Value)
                .SingleOrDefault(item => item.Key == key);
            if (current is null)
            {
                return Fail<Unit>("That saved screen no longer exists.");
            }

            var updated = new ScreenDefinition(
                current.Id,
                current.SchemaVersion,
                RequireName(EditorName, current.Name),
                EditorDescription,
                current.LayoutId,
                current.Panels,
                current.Tags,
                current.AgentPolicyOverride);
            var saved = await _catalog.SaveScreenAsync(updated, revision, cancellationToken);
            ApplyError(saved.Error);
            if (saved.IsSuccess)
            {
                CloseOverlay();
                return DefinitionStoreResult<Unit>.Success(Unit.Value);
            }

            return DefinitionStoreResult<Unit>.Failure(saved.Error!);
        }

        return Fail<Unit>("This definition type cannot be edited here.");
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await _catalog.DeleteAsync(key, revision, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteSavedScreenAsync(
        DefinitionKey key,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (key.Kind != ScreenDefinition.Kind)
        {
            var unsupported = DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                DefinitionStoreErrorCode.UnsupportedKind,
                "Only a saved screen can be deleted with saved-screen undo."));
            ApplyError(unsupported.Error);
            return unsupported;
        }

        var current = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Key == key);
        if (current is null)
        {
            var missing = DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                DefinitionStoreErrorCode.NotFound,
                "That saved screen no longer exists."));
            ApplyError(missing.Error);
            return missing;
        }

        if (current.Revision != revision)
        {
            var stale = DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                DefinitionStoreErrorCode.RevisionConflict,
                "That saved screen changed before it could be deleted.",
                current.Revision));
            ApplyError(stale.Error);
            return stale;
        }

        var deleted = current;
        var result = await _catalog.DeleteAsync(key, revision, cancellationToken);
        ApplyError(result.Error);
        if (result.IsSuccess)
        {
            SavedScreenDeleteUndo.Publish(deleted);
        }

        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
        UndoSavedScreenDeleteAsync(CancellationToken cancellationToken)
    {
        ClearError();
        var result = await SavedScreenDeleteUndo.UndoAsync(cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public void DismissSavedScreenDeleteUndo() => SavedScreenDeleteUndo.Dismiss();

    public bool IsDefinitionOpen(DefinitionKey key) =>
        RuntimeWorkspace is not null && _runtimeHistorySource?.SourceDefinition == key;

    public async ValueTask<HostResult<CloseScopeResult>> ClosePanelAsync(
        PanelInstanceId panelId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var multiplexed = OpenTerminalPanels().Where(panel => panel.Id == panelId).ToArray();
        var result = await SessionClient.CloseAsync(
            CloseScopeRequest.Panel(panelId, decision),
            NewContext(),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            { Value: CloseScopeResult.Completed })
        {
            await TerminateMultiplexersAsync(multiplexed, cancellationToken);
        }
        return result;
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseTabAsync(
        TabInstanceId tabId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var multiplexed = _openWorkspaces
            .SelectMany(workspace => workspace.Tabs)
            .Where(tab => tab.Id == tabId)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .ToArray();
        var result = await SessionClient.CloseAsync(
            CloseScopeRequest.Tab(tabId, decision),
            NewContext(),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            { Value: CloseScopeResult.Completed })
        {
            await TerminateMultiplexersAsync(multiplexed, cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// Ends every session belonging to one workspace, and nothing else.
    ///
    /// Scoped to the workspace rather than the window because the window holds
    /// several of them: closing the one you are pointing at must leave the rest
    /// running.
    /// </summary>
    public async ValueTask<HostResult<CloseScopeResult>> CloseWorkspaceAsync(
        WorkspaceInstanceId workspaceId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var multiplexed = _openWorkspaces
            .Where(workspace => workspace.Id == workspaceId)
            .SelectMany(workspace => workspace.Tabs)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .ToArray();
        var result = await SessionClient.CloseAsync(
            CloseScopeRequest.Workspace(workspaceId, decision),
            NewContext(),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            { Value: CloseScopeResult.Completed })
        {
            await TerminateMultiplexersAsync(multiplexed, cancellationToken);
        }
        return result;
    }

    public async Task CloseDetachedMultiplexedTerminalAsync(
        TerminalRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        await TerminateMultiplexersAsync([panel], cancellationToken);
    }

    private IEnumerable<TerminalRuntimePanelViewModel> OpenTerminalPanels() =>
        _openWorkspaces
            .SelectMany(workspace => workspace.Tabs)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>();

    private async Task TerminateMultiplexersAsync(
        IEnumerable<TerminalRuntimePanelViewModel> panels,
        CancellationToken cancellationToken)
    {
        if (_terminalMultiplexerCoordinator is null)
        {
            return;
        }

        foreach (var panel in panels)
        {
            if (panel.MultiplexerSession is not { IsEstablished: true } session)
            {
                continue;
            }

            var result = await _terminalMultiplexerCoordinator.TerminateAsync(
                panel.Connection,
                session,
                cancellationToken);
            if (!result.Terminated)
            {
                SetError(result.Detail);
            }
        }

        await RefreshManagedRemoteSessionsAsync(CancellationToken.None);
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseWindowAsync(
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var result = await SessionClient.CloseAsync(
            CloseScopeRequest.Window(WindowId, decision),
            NewContext(),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        return result;
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseFilePanelAsync(
        FileRuntimePanelViewModel panel,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (panel.HostedClient is null)
        {
            return HostResult<CloseScopeResult>.Fail(
                HostError.Create(
                    HostErrorCode.NotFound,
                    "This File Viewer has no hosted session to close."),
                currentRevision: 0);
        }

        var result = await panel.HostedClient.CloseAsync(decision, cancellationToken);
        RecordRecentSessionCompletions(result);
        return result;
    }

    public async Task<bool> RemovePanelAsync(
        PanelInstanceId panelId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var tab = workspace.Tabs.FirstOrDefault(item =>
                item.Panels.Any(panel => panel.Id == panelId));
            var panel = tab?.Panels.SingleOrDefault(item => item.Id == panelId);
            if (tab is null || panel is null)
            {
                return false;
            }

            if (tab.Panels.Count == 1)
            {
                return await RemoveTabUnderGateAsync(
                    workspace,
                    tab,
                    linkedCancellation.Token);
            }

            var proposal = BuildPanelRemovalProposal(
                workspace,
                tab.Id,
                panelId)
                ?? throw new InvalidOperationException(
                    "The runtime panel changed before removal could start.");
            return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                proposal,
                "panel removal",
                () =>
                {
                    StopTrackingRecovery(panel);
                    QueueRecentSessionCompletion(
                        panel.Id,
                        RecentSessionOutcome.GracefullyClosed);
                    if (!tab.RemovePanel(panelId))
                    {
                        throw new InvalidOperationException(
                            "The runtime panel changed before the host-approved removal was applied.");
                    }
                },
                RuntimeGraphStaleProposalHandling.RefreshAndRetry,
                linkedCancellation.Token,
                currentWorkspace => BuildPanelRemovalProposal(
                    currentWorkspace,
                    tab.Id,
                    panelId));
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> RemoveTabAsync(
        TabInstanceId tabId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || workspace.Tabs.SingleOrDefault(item => item.Id == tabId)
                    is not { } tab)
            {
                return false;
            }

            return await RemoveTabUnderGateAsync(
                workspace,
                tab,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    private async Task<bool> RemoveTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(RuntimeWorkspace, workspace)
            || !workspace.Tabs.Contains(tab))
        {
            return false;
        }

        // Closing the last tab leaves the question "what do I open" in its
        // place. A workspace with nothing in it is a blank window with a button
        // on it, which is what closing tabs one by one used to arrive at.
        //
        // Unless that is already all it holds: closing the launcher itself is
        // how a workspace is finished from the tab strip, so the button on it
        // always does something.
        if (workspace.Tabs.Count == 1 && !IsLauncherTab(tab))
        {
            return await ReplaceRuntimeTabUnderGateAsync(
                workspace,
                tab,
                _ => CreateLauncherTab(),
                "last tab removal",
                cancellationToken);
        }

        // The last launcher tab stays. Closing it would leave the window with
        // nothing in it and nothing to open anything from; ending the workspace
        // is the rail's job.
        if (workspace.Tabs.Count == 1)
        {
            return false;
        }

        var proposal = BuildTabRemovalProposal(workspace, tab.Id)
            ?? throw new InvalidOperationException(
                "The runtime tab changed before removal could start.");
        return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
            workspace,
            proposal,
            "tab removal",
            () =>
            {
                foreach (var panel in tab.Panels)
                {
                    StopTrackingRecovery(panel);
                    QueueRecentSessionCompletion(
                        panel.Id,
                        RecentSessionOutcome.GracefullyClosed);
                }

                // The tab before this one, or the one that took its place at the
                // front. Jumping to the first tab sent the user across the strip
                // every time they closed something near the end.
                var at = workspace.Tabs.IndexOf(tab);
                tab.DisposePanels();
                workspace.Tabs.Remove(tab);
                if (ReferenceEquals(workspace.ActiveTab, tab))
                {
                    workspace.ActiveTab = workspace.Tabs[Math.Max(0, at - 1)];
                }
            },
            RuntimeGraphStaleProposalHandling.RefreshAndRetry,
            cancellationToken,
            currentWorkspace => BuildTabRemovalProposal(
                currentWorkspace,
                tab.Id));
    }

    public async Task<bool> MoveActiveTabAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "A tab can move one position left or right.");
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || workspace.ActiveTab is not { } activeTab)
            {
                return false;
            }

            var sourceIndex = workspace.Tabs.IndexOf(activeTab);
            var anchorIndex = sourceIndex + offset;
            if (anchorIndex < 0 || anchorIndex >= workspace.Tabs.Count)
            {
                return false;
            }

            return await MoveTabUnderGateAsync(
                workspace,
                activeTab,
                workspace.Tabs[anchorIndex],
                offset < 0 ? RuntimeTabPlacement.Before : RuntimeTabPlacement.After,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> MoveTabAsync(
        TabInstanceId sourceTabId,
        TabInstanceId anchorTabId,
        RuntimeTabPlacement placement,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement), placement, null);
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var sourceTab = workspace.Tabs.SingleOrDefault(tab => tab.Id == sourceTabId);
            var anchorTab = workspace.Tabs.SingleOrDefault(tab => tab.Id == anchorTabId);
            if (sourceTab is null || anchorTab is null)
            {
                return false;
            }

            return await MoveTabUnderGateAsync(
                workspace,
                sourceTab,
                anchorTab,
                placement,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    // The caller owns _runtimeGraphGate and resolves source/anchor from the
    // post-wait collection. This keeps queued keyboard moves adjacent even
    // when an earlier drag changes the tab order while they wait.
    private async Task<bool> MoveTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel sourceTab,
        RuntimeTabViewModel anchorTab,
        RuntimeTabPlacement placement,
        CancellationToken cancellationToken)
    {
        var sourceIndex = workspace.Tabs.IndexOf(sourceTab);
        var anchorIndex = workspace.Tabs.IndexOf(anchorTab);
        if (sourceIndex < 0 || anchorIndex < 0)
        {
            return false;
        }

        var insertionIndex = placement == RuntimeTabPlacement.Before
            ? anchorIndex
            : anchorIndex + 1;
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        if (sourceIndex == insertionIndex)
        {
            return false;
        }

        var current = CaptureRuntimeWorkspaceGraph(workspace);
        var reorderedTabs = current.Tabs.ToList();
        var movedTab = reorderedTabs[sourceIndex];
        reorderedTabs.RemoveAt(sourceIndex);
        reorderedTabs.Insert(insertionIndex, movedTab);
        var proposal = new WorkspaceInstance(
            current.Id,
            current.Title,
            reorderedTabs,
            current.ActiveTabId);

        // Clear the live-region value only for a real attempt so a rejected
        // or cancelled proposal can never leave a fresh success announcement.
        TabReorderStatus = string.Empty;
        return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
            workspace,
            proposal,
            "tab reorder",
            () =>
            {
                workspace.Tabs.Move(sourceIndex, insertionIndex);
                TabReorderStatus =
                    $"Moved tab “{sourceTab.Title}” to position " +
                    $"{insertionIndex + 1} of {workspace.Tabs.Count}.";
                RefreshLauncherSearchResults();
            },
            RuntimeGraphStaleProposalHandling.Reject,
            cancellationToken);
    }

    public Task<bool> AddLocalTerminalPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddLocalTerminalPanelCoreAsync(null, cancellationToken);

    public Task<bool> AddLocalTerminalPanelAsync(
        PanelSplitOrientation orientation,
        CancellationToken cancellationToken = default) =>
        AddLocalTerminalPanelCoreAsync(orientation, cancellationToken);

    private async Task<bool> AddLocalTerminalPanelCoreAsync(
        PanelSplitOrientation? orientation,
        CancellationToken cancellationToken)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = tab?.ActivePanel is TerminalRuntimePanelViewModel terminal
            ? FindConnection(terminal.ConnectionId)
            : null;
        connection ??= _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (workspace is null || tab is null || connection is null)
        {
            SetError(orientation is null
                ? "Open a workspace with a local connection before adding a terminal panel."
                : "Open a workspace with a local connection before splitting a terminal panel.");
            return false;
        }

        var panel = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            "Terminal",
            PanelStartupBehavior.None);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            orientation is null ? "panel creation" : "panel split",
            () =>
            {
                if (orientation is { } splitOrientation)
                {
                    _ = tab.SplitActivePanel(panel, splitOrientation);
                }
                else
                {
                    tab.AddPanel(panel);
                    _ = tab.ActivatePanel(panel.Id);
                }

                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens a saved connection as a new panel in the active tab.
    ///
    /// A blank adapter is only one of the things a new panel can become: the
    /// launcher offers saved connections too, and choosing one has to open
    /// that connection rather than a default local shell.
    /// </summary>
    public async Task<bool> AddConnectionPanelAsync(
        ConnectionId id,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = FindConnection(id);
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return false;
        }

        var panel = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            connection.Name,
            PanelStartupBehavior.None);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "panel creation",
            () =>
            {
                tab.AddPanel(panel);
                _ = tab.ActivatePanel(panel.Id);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
            },
            cancellationToken);
    }

    /// <summary>
    /// Places an empty cell against one edge of the active tab.
    ///
    /// A placed cell is part of the workspace graph, so placing one is a graph
    /// mutation like any other rather than a local edit — the two sides holding
    /// different cells is what every placeholder bug has been made of.
    /// </summary>
    public async Task<bool> AddPlaceholderPanelAsync(
        PanelSide side,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a panel.");
            return false;
        }

        var placeholder = RuntimeTabViewModel.NewPlaceholder();
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            placeholder,
            "panel placement",
            () => tab.AddPlaceholder(side, placeholder),
            cancellationToken);
    }

    /// <summary>Divides a panel's own cell, leaving the new half empty.</summary>
    public async Task<bool> SplitPanelWithPlaceholderAsync(
        PanelInstanceId panelId,
        PanelSplitOrientation orientation,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before splitting a panel.");
            return false;
        }

        if (tab.Panels.All(panel => panel.Id != panelId))
        {
            return false;
        }

        var placeholder = RuntimeTabViewModel.NewPlaceholder();
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            placeholder,
            "panel split",
            () => tab.SplitWithPlaceholder(panelId, orientation, placeholder),
            cancellationToken);
    }

    /// <summary>
    /// Opens a tab that is nothing but the launcher.
    ///
    /// Asking what to open used to be a modal over the whole window. A tab whose
    /// one cell is unanswered says the same thing in the place the answer will
    /// land, and it can be left open, switched away from, and closed like any
    /// other tab.
    /// </summary>
    public Task<bool> AddLauncherTabAsync(CancellationToken cancellationToken = default)
    {
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before creating a tab.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            _ => CreateLauncherTab(),
            "launcher tab creation",
            cancellationToken);
    }

    private static RuntimeTabViewModel CreateLauncherTab()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "New tab", "Launcher");
        tab.AddPlaceholder(PanelSide.Right);
        return tab;
    }

    /// <summary>
    /// Whether this tab is only the launcher: one cell, still unanswered. What
    /// opens as a tab of its own opens here instead, because the user asked for
    /// something to open and this tab is the asking.
    /// </summary>
    private static bool IsLauncherTab(RuntimeTabViewModel tab) =>
        tab.Panels is [PanelPlaceholderViewModel];

    /// <summary>
    /// Opens a saved target as a panel in the active tab, with the adapter the
    /// launcher asked for.
    ///
    /// The tab-level counterpart is <see cref="AddSavedConnectionTabAsync"/>.
    /// Both exist because the same row means two things depending on where it
    /// was clicked: from a placed cell it fills that cell, and from the tab
    /// launcher it opens a tab.
    /// </summary>
    public Task<bool> AddSavedConnectionPanelAsync(
        SavedConnectionLaunchViewModel launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.Target switch
        {
            PanelConnectionOptionViewModel.Target.Connection connection =>
                AddConnectionPanelAsync(connection.Id, launch.Panel, cancellationToken),
            PanelConnectionOptionViewModel.Target.FileProvider fileProvider =>
                AddFileProviderPanelAsync(fileProvider.Id, launch.Panel, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(launch),
                launch.Target.GetType(),
                "The saved connection target is unsupported."),
        };
    }

    private async Task<bool> AddConnectionPanelAsync(
        ConnectionId id,
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == PanelKind.Terminal)
        {
            return await AddConnectionPanelAsync(id, cancellationToken);
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = FindConnection(id);
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return false;
        }

        if (!connection.Endpoint.PanelLaunchCapabilities.Supports(kind))
        {
            SetError($"{connection.Name} cannot open {PanelTitle(kind)}.");
            return false;
        }

        var title = PanelTitle(kind);
        var panel = kind switch
        {
            PanelKind.FileViewer => CreateFilePanel(
                workspace.Id,
                tab.Id,
                PanelInstanceId.New(),
                title,
                connection.Endpoint is ConnectionEndpoint.Ssh
                    ? ConnectionFileProviderProfiles.Id(connection.Id)
                    : BuiltInFileProviders.HomeId,
                deferInitialization: true,
                connection: connection),
            PanelKind.Statistics or PanelKind.ProcessMonitor => CreateMonitorPanel(
                workspace.Id,
                tab.Id,
                PanelInstanceId.New(),
                title,
                kind,
                connection),
            PanelKind.Docker => CreateDockerPanel(
                PanelInstanceId.New(),
                title,
                connection),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            $"{title} panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddFileProviderPanelAsync(
        FileProviderProfileId profileId,
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        var storedProfile = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (storedProfile is null)
        {
            SetError("That file connection no longer exists.");
            return false;
        }

        if (!storedProfile.Value.Configuration.PanelLaunchCapabilities.Supports(kind))
        {
            SetError($"{storedProfile.Value.Name} cannot open {PanelTitle(kind)}.");
            return false;
        }

        if (_filePanelClient.Profiles.All(profile => profile.Id != profileId.Value))
        {
            SetError("That file connection is not ready yet.");
            return false;
        }

        var panel = CreateFilePanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            PanelTitle(PanelKind.FileViewer),
            profileId,
            deferInitialization: true);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "File Viewer creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    /// <summary>
    /// Changes the connection behind an existing terminal while preserving the
    /// panel ID, tab, layout cell, and panel chrome. The caller closes the old
    /// hosted session first, including any required active-work confirmation.
    /// </summary>
    public bool ReplaceTerminalConnection(
        TerminalRuntimePanelViewModel currentPanel,
        ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ArgumentNullException.ThrowIfNull(connection);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That terminal panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels
            .OfType<TerminalRuntimePanelViewModel>()
            .SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null)
        {
            SetError("The terminal changed before its connection could be switched.");
            return false;
        }

        var replacement = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            livePanel.Title,
            PanelStartupBehavior.None,
            livePanel.Id);
        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The terminal changed before its connection could be switched.");
            return false;
        }

        workspace.AddConnections(Connections.Where(item => item.Id == connection.Id));
        StartTrackingRecovery(replacement);
        TrackRecentSession(replacement);
        RefreshAgentTerminalSelectionOptions(resetSelection: true);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool ReplaceTerminalConnection(
        TerminalRuntimePanelViewModel currentPanel,
        ConnectionId connectionId)
    {
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return false;
        }

        return ReplaceTerminalConnection(currentPanel, connection);
    }

    public bool ReplacePanelConnection(
        RuntimePanelViewModel currentPanel,
        ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ArgumentNullException.ThrowIfNull(connection);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels.SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null || livePanel.Kind != currentPanel.Kind)
        {
            SetError("The panel changed before its connection could be switched.");
            return false;
        }

        if (livePanel is DatabaseRuntimePanelViewModel)
        {
            // The database panel binds to saved database connections through
            // its own selector; a terminal connection is never its target.
            SetError("Choose a database connection from the panel selector.");
            return false;
        }

        RuntimePanelViewModel replacement;
        if (livePanel is FileRuntimePanelViewModel)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError(
                    "That execution connection cannot back File Viewer. "
                    + "Choose a file connection from the panel selector.");
                return false;
            }

            var profileId = connection.Endpoint is ConnectionEndpoint.Ssh
                ? ConnectionFileProviderProfiles.Id(connection.Id)
                : BuiltInFileProviders.HomeId;
            replacement = CreateFilePanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                profileId,
                connection: connection,
                deferInitialization: true);
        }
        else if (livePanel.Kind is PanelKind.Statistics or PanelKind.ProcessMonitor)
        {
            replacement = CreateMonitorPanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                livePanel.Kind,
                connection);
        }
        else if (livePanel is BrowserRuntimePanelViewModel browser)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError("A browser can use only a local or SSH connection.");
                return false;
            }

            replacement = CreateBrowserPanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                browser.CurrentAddress,
                connection);
        }
        else if (livePanel is DockerRuntimePanelViewModel)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError("A Docker panel can use only a local or SSH connection.");
                return false;
            }

            replacement = CreateDockerPanel(
                livePanel.Id,
                livePanel.Title,
                connection);
        }
        else
        {
            SetError("This panel type does not support connection switching.");
            return false;
        }

        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The panel changed before its connection could be switched.");
            return false;
        }

        workspace.AddConnections(Connections.Where(item => item.Id == connection.Id));
        StartTrackingRecovery(replacement);
        StartAcceptedRuntimePanel(replacement);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool ReplacePanelConnection(
        RuntimePanelViewModel currentPanel,
        ConnectionId connectionId)
    {
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return false;
        }

        return ReplacePanelConnection(currentPanel, connection);
    }

    public bool ReplaceFilePanelProfile(
        FileRuntimePanelViewModel currentPanel,
        FileProviderProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That File Viewer panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels
            .OfType<FileRuntimePanelViewModel>()
            .SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null)
        {
            SetError("The File Viewer changed before its connection could be switched.");
            return false;
        }

        var replacement = CreateFilePanel(
            workspace.Id,
            tab.Id,
            livePanel.Id,
            livePanel.Title,
            profileId,
            deferInitialization: true);
        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The File Viewer changed before its connection could be switched.");
            return false;
        }

        StartTrackingRecovery(replacement);
        StartAcceptedRuntimePanel(replacement);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public async Task<bool> AddFilePanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a File Viewer panel.");
            return false;
        }

        var panel = CreateFilePanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            "File Viewer",
            deferInitialization: true);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "File Viewer creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public async Task<bool> AddBrowserPanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a browser panel.");
            return false;
        }

        if (_browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return false;
        }

        var panel = CreateBrowserPanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            "Browser",
            BrowserAddress.Blank);
        if (panel is not BrowserRuntimePanelViewModel)
        {
            SetError("The embedded browser could not be initialized.");
            panel.Dispose();
            return false;
        }

        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "browser creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public Task<bool> AddStatisticsPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddMonitorPanelAsync(PanelKind.Statistics, cancellationToken);

    public Task<bool> AddProcessMonitorPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddMonitorPanelAsync(PanelKind.ProcessMonitor, cancellationToken);

    public async Task<bool> AddDatabasePanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a database panel.");
            return false;
        }

        var panel = CreateDatabasePanel(PanelInstanceId.New(), "Database");
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "Database panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public async Task<bool> AddDockerPanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a Docker panel.");
            return false;
        }

        var panel = CreateDockerPanel(PanelInstanceId.New(), "Docker");
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "Docker panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddMonitorPanelAsync(
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError($"Open a workspace tab before adding a {PanelTitle(kind)} panel.");
            return false;
        }

        var panel = CreateMonitorPanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            PanelTitle(kind),
            kind);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            $"{PanelTitle(kind)} creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddRuntimePanelUnderReceiptAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel,
        string operation,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(commit);

        var navigation = CaptureRuntimeMutationNavigation();
        var replacesLauncher = IsLauncherTab(tab);
        var firstPanelTitle = replacesLauncher
            ? tab.TitleForFirstPanel(panel.Title)
            : tab.Title;
        var attached = false;
        try
        {
            return await ReplaceRuntimeWorkspaceGraphAsync(
                workspace,
                operation,
                // Answering a placed cell swaps that cell for the panel; anything
                // else grows the tab. The two must not be confused: proposing an
                // append while the commit replaces would leave the host holding a
                // panel the client had already dropped.
                currentWorkspace =>
                {
                    var replacement = tab.ReplaceTarget is { } replacedPanelId
                        ? ReplaceRuntimePanel(
                            CaptureRuntimeWorkspaceGraph(currentWorkspace),
                            tab.Id,
                            replacedPanelId,
                            new PanelInstance(panel.Id, panel.Kind, panel.Title))
                        : AppendRuntimePanel(
                            CaptureRuntimeWorkspaceGraph(currentWorkspace),
                            tab.Id,
                            new PanelInstance(panel.Id, panel.Kind, panel.Title));
                    return !string.Equals(firstPanelTitle, tab.Title, StringComparison.Ordinal)
                        ? RenameRuntimeTab(replacement, tab.Id, firstPanelTitle)
                        : replacement;
                },
                () =>
                {
                    try
                    {
                        commit();
                    }
                    finally
                    {
                        attached = tab.Panels.Contains(panel);
                    }

                    if (attached)
                    {
                        if (replacesLauncher)
                        {
                            tab.AdoptFirstPanelTitle(panel.Title);
                        }

                        StartAcceptedRuntimePanel(panel);
                        CompleteRuntimeMutationNavigation(navigation);
                    }
                },
                cancellationToken);
        }
        finally
        {
            if (!attached)
            {
                panel.Dispose();
            }
        }
    }

    public Task<bool> AddConnectionTabAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = FindConnection(connectionId);
        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return Task.FromResult(false);
        }

        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentStored = _catalog.Snapshot.Connections.SingleOrDefault(
                    item => item.Value.Id == connectionId);
                if (currentStored is null)
                {
                    SetError("That connection no longer exists.");
                    return null;
                }

                var currentConnection = currentStored.Value;
                var currentLaunchItem = ToConnectionItem(
                    currentConnection,
                    currentStored.Revision);
                if (currentLaunchItem is not { CanOpen: true })
                {
                    SetError(
                        currentLaunchItem?.Status
                        ?? "That connection is unavailable on this platform.");
                    return null;
                }

                return CreateConnectionTab(
                    runtime.Id,
                    currentConnection,
                    agentPolicy: runtime.AgentPolicy);
            },
            "connection tab creation",
            cancellationToken);
    }

    public Task<bool> AddSavedConnectionTabAsync(
        SavedConnectionLaunchViewModel launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.Target switch
        {
            PanelConnectionOptionViewModel.Target.Connection connection =>
                AddConnectionPanelTabAsync(
                    connection.Id,
                    launch.Panel,
                    cancellationToken),
            PanelConnectionOptionViewModel.Target.FileProvider fileProvider =>
                AddFileProviderTabAsync(
                    fileProvider.Id,
                    launch.Panel,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(launch),
                launch.Target.GetType(),
                "The saved connection target is unsupported."),
        };
    }

    private Task<bool> AddConnectionPanelTabAsync(
        ConnectionId connectionId,
        PanelKind panel,
        CancellationToken cancellationToken)
    {
        if (panel == PanelKind.Terminal)
        {
            return AddConnectionTabAsync(connectionId, cancellationToken);
        }

        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = FindConnection(connectionId);
        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (connection is null)
        {
            SetError("That connection no longer exists.");
            return Task.FromResult(false);
        }

        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return Task.FromResult(false);
        }

        if (!connection.Endpoint.PanelLaunchCapabilities.Supports(panel))
        {
            SetError($"{connection.Name} cannot open {PanelTitle(panel)}.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentConnection = FindConnection(connectionId);
                if (currentConnection is null)
                {
                    SetError("That connection no longer exists.");
                    return null;
                }

                if (!currentConnection.Endpoint.PanelLaunchCapabilities.Supports(panel))
                {
                    SetError($"{currentConnection.Name} cannot open {PanelTitle(panel)}.");
                    return null;
                }

                return CreateConnectionPanelTab(
                    runtime.Id,
                    currentConnection,
                    panel,
                    runtime.AgentPolicy);
            },
            $"{PanelTitle(panel)} connection tab creation",
            cancellationToken);
    }

    private Task<bool> AddFileProviderTabAsync(
        FileProviderProfileId profileId,
        PanelKind panel,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var storedProfile = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (storedProfile is null)
        {
            SetError("That file connection no longer exists.");
            return Task.FromResult(false);
        }

        if (!storedProfile.Value.Configuration.PanelLaunchCapabilities.Supports(panel))
        {
            SetError($"{storedProfile.Value.Name} cannot open {PanelTitle(panel)}.");
            return Task.FromResult(false);
        }

        if (_filePanelClient.Profiles.All(profile => profile.Id != profileId.Value))
        {
            SetError("That file connection is not ready yet.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentProfile = _catalog.Snapshot.FileProviderProfiles
                    .SingleOrDefault(item => item.Value.Id == profileId);
                if (currentProfile is null)
                {
                    SetError("That file connection no longer exists.");
                    return null;
                }

                return CreateFileProviderTab(runtime.Id, currentProfile.Value);
            },
            "file connection tab creation",
            cancellationToken);
    }

    public Task<bool> AddScreenTabAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default) =>
        OpenScreenTabAsync(screenId, replacedTab: null, cancellationToken);

    private Task<bool> ReplaceScreenTabAsync(
        RuntimeTabViewModel replacedTab,
        ScreenId screenId,
        CancellationToken cancellationToken) =>
        OpenScreenTabAsync(screenId, replacedTab, cancellationToken);

    private Task<bool> OpenScreenTabAsync(
        ScreenId screenId,
        RuntimeTabViewModel? replacedTab,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var storedScreen = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved screen as a tab.");
            return Task.FromResult(false);
        }

        if (storedScreen is null)
        {
            SetError("That saved screen no longer exists.");
            return Task.FromResult(false);
        }

        return replacedTab is null
            ? AppendRuntimeTabAsync(workspace, CreateScreenTab, "saved-screen tab creation", cancellationToken)
            : ReplaceRuntimeTabAsync(
                workspace,
                replacedTab,
                CreateScreenTab,
                "saved-screen tab creation",
                cancellationToken);

        RuntimeTabViewModel? CreateScreenTab(RuntimeWorkspaceViewModel runtime)
        {
            var currentStoredScreen = _catalog.Snapshot.Screens
                .SingleOrDefault(item => item.Value.Id == screenId);
            if (currentStoredScreen is null)
            {
                SetError("That saved screen no longer exists.");
                return null;
            }

            var currentScreen = currentStoredScreen.Value;
            return CreateRuntimeTab(
                runtime.Id,
                currentScreen.Name,
                "Saved screen",
                currentScreen.LayoutId,
                currentScreen.Panels,
                currentScreen.Key,
                currentScreen.Name,
                runtime.AgentPolicy.WithOverride(
                    currentScreen.AgentPolicyOverride,
                    currentScreen.Key,
                    currentStoredScreen.Revision));
        }
    }

    public Task<bool> AddLocalTerminalTabAsync(
        CancellationToken cancellationToken = default)
    {
        if (HasOverlay)
        {
            SetError("Close the current overlay before creating a terminal tab.");
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = ActivePanel is TerminalRuntimePanelViewModel terminal
            ? FindConnection(terminal.ConnectionId)
            : null;
        connection ??= _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (workspace is null || connection is null)
        {
            SetError("Open a workspace with a local connection before creating a tab.");
            return Task.FromResult(false);
        }

        var connectionId = connection.Id;
        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentConnection = FindConnection(connectionId);
                if (currentConnection is null)
                {
                    SetError("The local connection no longer exists.");
                    return null;
                }

                return CreateConnectionTab(
                    runtime.Id,
                    currentConnection,
                    agentPolicy: runtime.AgentPolicy);
            },
            "tab creation",
            cancellationToken);
    }

    public Task<bool> AddBrowserTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Browser, cancellationToken);

    public Task<bool> AddFileViewerTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.FileViewer, cancellationToken);

    public Task<bool> AddStatisticsTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Statistics, cancellationToken);

    public Task<bool> AddDatabaseTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.DatabaseViewer, cancellationToken);

    public Task<bool> AddDockerTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Docker, cancellationToken);

    public Task<bool> AddProcessMonitorTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.ProcessMonitor, cancellationToken);

    /// <summary>
    /// Appends a one-panel tab for a local adapter. The New Tab catalog uses this
    /// path; the visually identical catalog inside a placed placeholder continues
    /// to use the panel-creation methods instead.
    /// </summary>
    private Task<bool> AddSinglePanelTabAsync(
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        if (kind is not (PanelKind.Browser
            or PanelKind.FileViewer
            or PanelKind.Statistics
            or PanelKind.ProcessMonitor
            or PanelKind.DatabaseViewer
            or PanelKind.Docker))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ClearError();
        if (HasOverlay)
        {
            SetError("Close the current overlay before creating a tab.");
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before creating a tab.");
            return Task.FromResult(false);
        }

        if (kind == PanelKind.Browser && _browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime => CreateSinglePanelTab(runtime.Id, kind),
            $"{SinglePanelTabTitle(kind)} tab creation",
            cancellationToken);
    }

    private RuntimeTabViewModel? CreateSinglePanelTab(
        WorkspaceInstanceId workspaceId,
        PanelKind kind)
    {
        var title = SinglePanelTabTitle(kind);
        var source = kind is PanelKind.Statistics or PanelKind.ProcessMonitor
            ? "Local host"
            : "Local";
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), title, source);
        try
        {
            var panel = kind switch
            {
                PanelKind.Browser => CreateBrowserPanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    BrowserAddress.Blank),
                PanelKind.FileViewer => CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    deferInitialization: true),
                PanelKind.Statistics or PanelKind.ProcessMonitor =>
                    CreateMonitorPanel(
                        workspaceId,
                        tab.Id,
                        PanelInstanceId.New(),
                        title,
                        kind),
                PanelKind.DatabaseViewer => CreateDatabasePanel(
                    PanelInstanceId.New(),
                    title),
                PanelKind.Docker => CreateDockerPanel(
                    PanelInstanceId.New(),
                    title),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            if (kind == PanelKind.Browser
                && panel is not BrowserRuntimePanelViewModel)
            {
                SetError("The embedded browser could not be initialized.");
                panel.Dispose();
                return null;
            }

            AddPanelOrDispose(tab, panel);
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private static string SinglePanelTabTitle(PanelKind kind) => kind switch
    {
        PanelKind.Browser => "Browser",
        PanelKind.FileViewer => "File Viewer",
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        PanelKind.DatabaseViewer => "Database",
        PanelKind.Docker => "Docker",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private bool CanAppendSavedDefinitionTab()
    {
        if (Overlay is ShellOverlay.None or ShellOverlay.CommandPalette)
        {
            return true;
        }

        SetError("Close the current editor before adding a saved tab.");
        return false;
    }

    private async Task<bool> AppendRuntimeTabAsync(
        RuntimeWorkspaceViewModel workspace,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(createTab);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var navigation = CaptureRuntimeMutationNavigation();
        RuntimeTabViewModel? tab = null;
        var committed = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            tab = createTab(workspace);
            if (tab is null)
            {
                return false;
            }

            var current = CaptureRuntimeWorkspaceGraph(workspace);
            var proposal = new WorkspaceInstance(
                current.Id,
                current.Title,
                current.Tabs.Append(CaptureRuntimeTab(tab)),
                tab.Id);
            return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                proposal,
                operation,
                () =>
                {
                    // The host accepted this tab. Transfer ownership before
                    // publishing it so a later projection-side exception cannot
                    // dispose panels that are already part of the live graph.
                    committed = true;
                    CommitRuntimeTabAppend(workspace, tab);
                    CompleteRuntimeMutationNavigation(navigation);
                },
                RuntimeGraphStaleProposalHandling.RefreshAndRetry,
                linkedCancellation.Token,
                currentWorkspace => BuildTabAppendProposal(
                    currentWorkspace,
                    tab));
        }
        finally
        {
            _runtimeGraphGate.Release();
            if (!committed)
            {
                tab?.DisposePanels();
            }
        }
    }

    /// <summary>
    /// Puts a tab where a launcher tab was standing.
    ///
    /// A launcher tab is the question "what do I open"; a saved screen is an
    /// answer to it. Appending beside it instead left the question sitting there
    /// next to its own answer, and the user closing it by hand every time.
    ///
    /// Only a launcher tab is ever replaced this way. It holds one unanswered
    /// cell and therefore no session, so nothing is lost when it goes.
    /// </summary>
    private async Task<bool> ReplaceRuntimeTabAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            return await ReplaceRuntimeTabUnderGateAsync(
                workspace,
                replacedTab,
                createTab,
                operation,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    /// <summary>
    /// The gate is already held. Closing the last tab reaches this from inside
    /// tab removal, and the gate is not reentrant — taking it a second time
    /// waits for a release that cannot come until the wait returns.
    /// </summary>
    private async Task<bool> ReplaceRuntimeTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacedTab);
        ArgumentNullException.ThrowIfNull(createTab);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var navigation = CaptureRuntimeMutationNavigation();
        RuntimeTabViewModel? tab = null;
        var committed = false;
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || !workspace.Tabs.Contains(replacedTab))
            {
                return false;
            }

            tab = createTab(workspace);
            if (tab is null)
            {
                return false;
            }

            return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                BuildTabReplacementProposal(workspace, replacedTab, tab)!,
                operation,
                () =>
                {
                    committed = true;
                    var at = workspace.Tabs.IndexOf(replacedTab);
                    workspace.Tabs[at] = tab;
                    foreach (var panel in replacedTab.Panels)
                    {
                        StopTrackingRecovery(panel);
                        QueueRecentSessionCompletion(
                            panel.Id,
                            RecentSessionOutcome.GracefullyClosed);
                    }

                    replacedTab.DisposePanels();
                    foreach (var panel in tab.Panels)
                    {
                        StartTrackingRecovery(panel);
                    }

                    TrackRecentSessions(tab.Panels);
                    workspace.ActiveTab = tab;
                    foreach (var panel in tab.Panels)
                    {
                        StartAcceptedRuntimePanel(panel);
                    }

                    CompleteRuntimeMutationNavigation(navigation);
                },
                RuntimeGraphStaleProposalHandling.RefreshAndRetry,
                cancellationToken,
                currentWorkspace => BuildTabReplacementProposal(
                    currentWorkspace,
                    replacedTab,
                    tab));
        }
        finally
        {
            if (!committed)
            {
                tab?.DisposePanels();
            }
        }
    }

    private static WorkspaceInstance? BuildTabReplacementProposal(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacedTab);
        ArgumentNullException.ThrowIfNull(tab);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        if (current.Tabs.All(item => item.Id != replacedTab.Id))
        {
            return null;
        }

        var captured = CaptureRuntimeTab(tab);
        return new WorkspaceInstance(
            current.Id,
            current.Title,
            current.Tabs.Select(item => item.Id == replacedTab.Id ? captured : item),
            tab.Id);
    }

    private void CommitRuntimeTabAppend(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab)
    {
        workspace.Tabs.Add(tab);
        foreach (var panel in tab.Panels)
        {
            StartTrackingRecovery(panel);
        }

        var connectionIds = tab.Panels
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.ConnectionId)
            .ToHashSet();
        workspace.AddConnections(Connections.Where(connection =>
            connectionIds.Contains(connection.Id)));
        TrackRecentSessions(tab.Panels);
        workspace.ActiveTab = tab;
        foreach (var panel in tab.Panels)
        {
            StartAcceptedRuntimePanel(panel);
        }
    }

    private RuntimeMutationNavigationSnapshot CaptureRuntimeMutationNavigation() =>
        new(Route, Overlay, _overlayRevision);

    private void CompleteRuntimeMutationNavigation(
        RuntimeMutationNavigationSnapshot initiatingState)
    {
        if (Overlay is ShellOverlay.DefinitionEditor or ShellOverlay.LayoutDesigner)
        {
            return;
        }

        var initiatingOverlayStillOpen =
            _overlayRevision == initiatingState.OverlayRevision
            && Overlay == initiatingState.Overlay;
        var initiatingOverlayWasDismissed =
            initiatingState.Overlay != ShellOverlay.None
            && Overlay == ShellOverlay.None
            && Route == initiatingState.Route;
        var initiatingSurfaceIsUnchanged =
            initiatingState.Overlay == ShellOverlay.None
            && Overlay == ShellOverlay.None
            && Route == initiatingState.Route;
        if (!initiatingOverlayStillOpen
            && !initiatingOverlayWasDismissed
            && !initiatingSurfaceIsUnchanged)
        {
            return;
        }

        Route = ShellRoute.Workspace;
        if (initiatingOverlayStillOpen
            && initiatingState.Overlay is ShellOverlay.CommandPalette
                or ShellOverlay.NewPanel)
        {
            CloseOverlay();
        }
    }

    public async Task<bool> SelectRelativeTabAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (HasOverlay)
        {
            SetError("Close the current overlay before changing tabs.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        if (workspace?.ActiveTab is null || workspace.Tabs.Count < 2)
        {
            return false;
        }

        var current = workspace.Tabs.IndexOf(workspace.ActiveTab);
        var destination = (current + offset + workspace.Tabs.Count) % workspace.Tabs.Count;
        return await ActivateTabAsync(workspace.Tabs[destination].Id, cancellationToken);
    }

    public Task<bool> SelectLastActiveTabAsync(CancellationToken cancellationToken = default)
    {
        return RuntimeWorkspace?.LastActiveTab is { } lastActiveTab
            ? ActivateTabAsync(lastActiveTab.Id, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> SelectTabAtPositionAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null || position < 0 || position >= workspace.Tabs.Count)
        {
            return Task.FromResult(false);
        }

        return ActivateTabAsync(workspace.Tabs[position].Id, cancellationToken);
    }

    public Task<bool> FocusPanelAsync(
        PanelFocusDirection direction,
        CancellationToken cancellationToken = default)
    {
        var panelId = RuntimeWorkspace?.ActiveTab?.FindPanel(direction);
        if (panelId is null)
        {
            return Task.FromResult(false);
        }

        return ActivatePanelAsync(panelId.Value, cancellationToken);
    }

    public bool ToggleActivePanelZoom()
    {
        if (RuntimeWorkspace?.ActiveTab?.ToggleActivePanelZoom() != true)
        {
            return false;
        }

        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public async Task<bool> RenameActiveTabAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var tab = RuntimeWorkspace?.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        return await UpdateRuntimeTabIdentityAsync(
            tab.Id,
            title,
            tab.Icon,
            cancellationToken);
    }

    public async Task<bool> UpdateRuntimeTabIdentityAsync(
        TabInstanceId tabId,
        string title,
        string icon,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (workspace is null
            || tab is null
            || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = title.Trim();
        var normalizedIcon = WorkspaceIcons.OptionFor(icon).Id;
        var titleChanged = !string.Equals(tab.Title, normalizedTitle, StringComparison.Ordinal);
        var iconChanged = !string.Equals(tab.Icon, normalizedIcon, StringComparison.Ordinal);
        if (!titleChanged && !iconChanged)
        {
            return true;
        }

        if (!titleChanged)
        {
            _ = tab.ChooseIcon(normalizedIcon);
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        return await ReplaceRuntimeWorkspaceGraphAsync(
            workspace,
            "tab identity update",
            currentWorkspace =>
            {
                if (!currentWorkspace.Tabs.Contains(tab))
                {
                    return null;
                }

                return RenameRuntimeTab(
                    CaptureRuntimeWorkspaceGraph(currentWorkspace),
                    tab.Id,
                    normalizedTitle);
            },
            () =>
            {
                if (!tab.Rename(normalizedTitle))
                {
                    throw new InvalidOperationException(
                        "The runtime tab changed before the host-approved identity was applied.");
                }

                if (iconChanged)
                {
                    _ = tab.ChooseIcon(normalizedIcon);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Records an explicit icon choice even when the user picked the icon that
    /// was already displayed. Equality says whether pixels change, not whether
    /// the first panel still owns this field.
    /// </summary>
    public bool ChooseRuntimeTabIcon(TabInstanceId tabId, string icon)
    {
        var tab = RuntimeWorkspace?.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (tab is null)
        {
            return false;
        }

        _ = tab.ChooseIcon(icon);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool EnterTerminalCopyMode() =>
        (ActivePanel as TerminalRuntimePanelViewModel)?.EnterCopyMode() == true;

    public bool ExitTerminalCopyMode() =>
        (ActivePanel as TerminalRuntimePanelViewModel)?.ExitCopyMode() == true;

    public void ClearError() => OperationError = null;

    public void ShowApplicationKeySequenceHint(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ApplicationKeySequenceHint = message.Trim();
    }

    public void ClearApplicationKeySequenceHint() => ApplicationKeySequenceHint = null;

    /// <summary>
    /// Raises an operation error.
    ///
    /// It is also written to standard error. The banner is transient, sits against
    /// the window edge, and has been seen showing with nothing legible in it — an
    /// error the user cannot read is the same as no error at all, and the text is
    /// the only thing that says what went wrong.
    /// </summary>
    public void SetError(string message)
    {
        OperationError = message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            Console.Error.WriteLine($"[ghostshell:error] {message}");
        }
    }

    public void SetDefinitionBundleStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        DefinitionBundleStatus = message.Trim();
    }

    private void OnCatalogChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RefreshCatalog(_catalog.Snapshot);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshCatalog(_catalog.Snapshot));
        }
    }

    private void OnFileTransfersChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RefreshFileTransfers();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshFileTransfers);
        }
    }

    private void OnFileProviderProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RefreshFileProviderDefinitions(_catalog.Snapshot);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                RefreshFileProviderDefinitions(_catalog.Snapshot));
        }
    }

    private void OnAiProviderProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RefreshAiProviderDefinitions(_catalog.Snapshot);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                RefreshAiProviderDefinitions(_catalog.Snapshot));
        }
    }

    private void StartTrackingAgentTerminalSelection(
        RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        workspace.Tabs.CollectionChanged += OnAgentSelectionTabsChanged;
        ReconcileAgentTerminalSelectionSubscriptions(workspace);
    }

    private void StopTrackingAgentTerminalSelection(
        RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is not null)
        {
            workspace.Tabs.CollectionChanged -= OnAgentSelectionTabsChanged;
        }

        foreach (var tab in _agentSelectionTrackedTabs)
        {
            tab.Panels.CollectionChanged -= OnAgentSelectionPanelsChanged;
        }

        foreach (var terminal in _agentSelectionTrackedTerminals)
        {
            terminal.PropertyChanged -= OnAgentSelectionTerminalPropertyChanged;
        }

        _agentSelectionTrackedTabs.Clear();
        _agentSelectionTrackedTerminals.Clear();
    }

    private void ReconcileAgentTerminalSelectionSubscriptions(
        RuntimeWorkspaceViewModel workspace)
    {
        foreach (var tab in _agentSelectionTrackedTabs)
        {
            tab.Panels.CollectionChanged -= OnAgentSelectionPanelsChanged;
        }

        foreach (var terminal in _agentSelectionTrackedTerminals)
        {
            terminal.PropertyChanged -= OnAgentSelectionTerminalPropertyChanged;
        }

        _agentSelectionTrackedTabs.Clear();
        _agentSelectionTrackedTerminals.Clear();
        foreach (var tab in workspace.Tabs)
        {
            tab.Panels.CollectionChanged += OnAgentSelectionPanelsChanged;
            _agentSelectionTrackedTabs.Add(tab);
            foreach (var terminal in tab.Panels.OfType<TerminalRuntimePanelViewModel>())
            {
                terminal.PropertyChanged += OnAgentSelectionTerminalPropertyChanged;
                _agentSelectionTrackedTerminals.Add(terminal);
            }
        }
    }

    private void OnAgentSelectionTabsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (RuntimeWorkspace is not { } workspace)
        {
            return;
        }

        ReconcileAgentTerminalSelectionSubscriptions(workspace);
        RefreshAgentTerminalSelectionOptions(resetSelection: false);
    }

    private void OnAgentSelectionPanelsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (RuntimeWorkspace is not { } workspace)
        {
            return;
        }

        ReconcileAgentTerminalSelectionSubscriptions(workspace);
        RefreshAgentTerminalSelectionOptions(resetSelection: false);
    }

    private void OnAgentSelectionTerminalPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName
                == nameof(TerminalRuntimePanelViewModel.SessionRequest)
            && sender is TerminalRuntimePanelViewModel { SessionRequest: not null } terminal)
        {
            TrackRecentSession(terminal);
        }

        if (eventArgs.PropertyName is
            nameof(TerminalRuntimePanelViewModel.ConnectionState)
            or nameof(TerminalRuntimePanelViewModel.SessionRequest)
            or nameof(TerminalRuntimePanelViewModel.HasObservedActiveSession))
        {
            RefreshAgentTerminalSelectionOptions(resetSelection: false);
        }
    }

    private void RefreshAgentTerminalSelectionOptions(bool resetSelection)
    {
        var selected = resetSelection
            ? []
            : AgentTerminalSelectionOptions
                .Where(option => option.IsSelected)
                .Select(option => (option.TabId, option.PanelId))
                .ToHashSet();
        var candidates = RuntimeWorkspace?.Tabs
            .SelectMany(tab => tab.Panels
                .OfType<TerminalRuntimePanelViewModel>()
                .Where(IsLiveAgentTerminal)
                .Select(terminal => (Tab: tab, Terminal: terminal)))
            .ToArray()
            ?? [];
        var candidateIds = candidates
            .Select(candidate => (candidate.Tab.Id, candidate.Terminal.Id))
            .ToHashSet();
        var lostSelection = !resetSelection
            && selected.Any(id => !candidateIds.Contains(id));

        AgentTerminalSelectionOptions.Clear();
        foreach (var candidate in candidates)
        {
            AgentTerminalSelectionOptions.Add(
                new AgentTerminalSelectionItemViewModel(
                    candidate.Tab.Id,
                    candidate.Tab.Title,
                    candidate.Terminal.Id,
                    candidate.Terminal.Title,
                    selected.Contains((candidate.Tab.Id, candidate.Terminal.Id)),
                    CanApplyAgentTerminalSelection,
                    OnAgentTerminalSelectionChanged));
        }

        if (resetSelection)
        {
            _agentTerminalSelectionStale = false;
            HasAgentTerminalSelectionError = false;
        }

        if (lostSelection)
        {
            SetAgentTerminalSelectionError(
                "A selected terminal is no longer live. Review the selected terminals before sending.",
                stale: true);
        }
        else
        {
            UpdateAgentTerminalSelectionStatus();
        }

        OnPropertyChanged(nameof(HasAgentTerminalSelectionOptions));
        NotifyAgentTerminalSelectionCountChanged();
    }

    private bool CanApplyAgentTerminalSelection(
        AgentTerminalSelectionItemViewModel option,
        bool selected)
    {
        if (AgentChat is not { CanChangeProvider: true }
            || !AgentTerminalSelectionOptions.Contains(option))
        {
            return false;
        }

        if (selected
            && AgentSelectedTerminalCount
                >= AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            SetAgentTerminalSelectionError(
                $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.",
                stale: false);
            return false;
        }

        return true;
    }

    private void OnAgentTerminalSelectionChanged()
    {
        _agentTerminalSelectionStale = false;
        HasAgentTerminalSelectionError = false;
        NotifyAgentTerminalSelectionCountChanged();
        UpdateAgentTerminalSelectionStatus();
    }

    private void NotifyAgentTerminalSelectionCountChanged()
    {
        OnPropertyChanged(nameof(AgentSelectedTerminalCount));
        OnPropertyChanged(nameof(AgentTerminalSelectionSummary));
    }

    private void UpdateAgentTerminalSelectionStatus()
    {
        if (_agentTerminalSelectionStale)
        {
            HasAgentTerminalSelectionError = true;
            AgentTerminalSelectionStatus =
                "A selected terminal is no longer live. Review the selected terminals before sending.";
            return;
        }

        if (AgentTerminalSelectionOptions.Count == 0)
        {
            HasAgentTerminalSelectionError = false;
            AgentTerminalSelectionStatus =
                "No live terminal sessions are available in this workspace.";
            return;
        }

        HasAgentTerminalSelectionError = false;
        AgentTerminalSelectionStatus = AgentSelectedTerminalCount switch
        {
            0 =>
                $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} live terminals from this workspace.",
            1 => "1 terminal selected. The selection locks when the run starts.",
            var count =>
                $"{count} terminals selected. The selection locks when the run starts.",
        };
    }

    private void SetAgentTerminalSelectionError(string message, bool stale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (stale)
        {
            _agentTerminalSelectionStale = true;
        }

        HasAgentTerminalSelectionError = true;
        AgentTerminalSelectionStatus = message;
    }

    private void StartTrackingRecovery(RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        foreach (var panel in workspace.Tabs.SelectMany(tab => tab.Panels))
        {
            StartTrackingRecovery(panel);
        }

        foreach (var tab in workspace.Tabs)
        {
            tab.PropertyChanged -= OnRecoveryRelevantTabPropertyChanged;
            tab.PropertyChanged += OnRecoveryRelevantTabPropertyChanged;
        }
    }

    private void StartTrackingRecovery(RuntimePanelViewModel panel)
    {
        if (panel is FileRuntimePanelViewModel
            or BrowserRuntimePanelViewModel
            or DatabaseRuntimePanelViewModel
            or TerminalRuntimePanelViewModel)
        {
            panel.PropertyChanged += OnRecoveryRelevantPanelPropertyChanged;
        }
    }

    private void StopTrackingRecovery(RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        foreach (var panel in workspace.Tabs.SelectMany(tab => tab.Panels))
        {
            StopTrackingRecovery(panel);
        }

        foreach (var tab in workspace.Tabs)
        {
            tab.PropertyChanged -= OnRecoveryRelevantTabPropertyChanged;
        }
    }

    private void StopTrackingRecovery(RuntimePanelViewModel panel)
    {
        if (panel is FileRuntimePanelViewModel
            or BrowserRuntimePanelViewModel
            or DatabaseRuntimePanelViewModel
            or TerminalRuntimePanelViewModel)
        {
            panel.PropertyChanged -= OnRecoveryRelevantPanelPropertyChanged;
        }
    }

    private void OnRecoveryRelevantPanelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (sender switch
        {
            FileRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(FileRuntimePanelViewModel.SelectedProfile)
                or nameof(FileRuntimePanelViewModel.CurrentLocation)
                or nameof(FileRuntimePanelViewModel.ShowHidden),
            BrowserRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(BrowserRuntimePanelViewModel.CurrentAddress)
                or nameof(BrowserRuntimePanelViewModel.ConnectionId),
            DatabaseRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(DatabaseRuntimePanelViewModel.RecoveryTarget)
                or nameof(DatabaseRuntimePanelViewModel.TunnelConnectionId),
            TerminalRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(TerminalRuntimePanelViewModel.MultiplexerSession),
            _ => false,
        } is false)
        {
            return;
        }

        QueueRuntimeRecoverySnapshot();
    }

    private void OnRecoveryRelevantTabPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (sender is RuntimeTabViewModel
            && eventArgs.PropertyName == nameof(RuntimeTabViewModel.DockLayoutRevision))
        {
            QueueRuntimeRecoverySnapshot();
        }
    }

    private void QueueRuntimeRecoverySnapshot()
    {
        QueueWorkspaceAutoSave();
        if (_runtimeRecoveryWriter is null || _shutdownStarted)
        {
            return;
        }

        string payload;
        try
        {
            payload = RuntimeWorkspaceRecoveryCodec.Serialize(
                RuntimeWorkspace,
                _runtimeHistorySource);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(
                $"[ghostshell:recovery] Runtime recovery snapshot preparation failed: {exception}");
            // The reason, not just the fact. Every message this can carry is one
            // of the codec's own validation sentences — no paths, no session
            // content — and without it the banner names a subsystem and leaves
            // the person reading it nothing to act on or report.
            SetError($"Runtime recovery state could not be prepared. {exception.Message}");
            return;
        }

        var queued = _runtimeRecoveryWriter.Enqueue(
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            payload);
        if (!queued.IsSuccess)
        {
            OnRuntimeRecoveryWriteFailed(
                _runtimeRecoveryWriter,
                new RuntimeRecoveryWriteFailedEventArgs(queued.Error!));
        }
    }

    private void OnRuntimeRecoveryWriteFailed(
        object? sender,
        RuntimeRecoveryWriteFailedEventArgs eventArgs)
    {
        _ = sender;
        Console.Error.WriteLine(
            $"[ghostshell:recovery] Runtime recovery write failed: "
            + $"{eventArgs.Error.Code}: {eventArgs.Error.Message}");
        void Apply() => SetError($"Runtime recovery is unavailable ({eventArgs.Error.Code}).");

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        }
    }

    private const int WorkspaceAutoSaveDebounceMilliseconds = 1500;

    private sealed record WorkspaceAutoSaveCapture(
        WorkspaceDefinition Workspace,
        long WorkspaceRevision,
        IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)> Layouts);

    /// <summary>
    /// Schedules a write-back of the live tabs into the open workspace's durable
    /// definition. Piggybacks on the recovery-snapshot triggers, so anything worth
    /// recovering is also worth persisting; the debounce coalesces drag storms
    /// into one save.
    /// </summary>
    private void QueueWorkspaceAutoSave()
    {
        if (_shutdownStarted || AutoSaveSourceWorkspace() is null)
        {
            return;
        }

        _workspaceAutoSaveDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _workspaceAutoSaveDebounce = debounce;
        _ = AutoSaveWorkspaceAsync(debounce.Token);
    }

    private StoredDefinition<WorkspaceDefinition>? AutoSaveSourceWorkspace()
    {
        if (_runtimeHistorySource?.SourceDefinition is not { } sourceKey
            || sourceKey.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        var stored = _catalog.Snapshot.Workspaces
            .SingleOrDefault(item => item.Value.Id.Value == sourceKey.Value);
        return stored is { Value.AutoSave: true } ? stored : null;
    }

    private async Task AutoSaveWorkspaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WorkspaceAutoSaveDebounceMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || _shutdownStarted)
        {
            return;
        }

        await PersistWorkspaceAutoSaveAsync();
    }

    /// <summary>
    /// Writes the pending autosave now instead of when the debounce elapses.
    ///
    /// Leaving a workspace is exactly when the debounce would be lost: it fires
    /// against whichever workspace is active at the time, so a switch a second
    /// after a change used to save the wrong one — or nothing. Every path that
    /// changes which workspace is in front flushes first.
    /// </summary>
    private async Task FlushWorkspaceAutoSaveAsync()
    {
        var pending = _workspaceAutoSaveDebounce;
        if (pending is null || pending.IsCancellationRequested || _shutdownStarted)
        {
            return;
        }

        pending.Cancel();
        _workspaceAutoSaveDebounce = null;
        await PersistWorkspaceAutoSaveAsync();
    }

    private async Task PersistWorkspaceAutoSaveAsync()
    {
        if (AutoSaveSourceWorkspace() is not { } stored)
        {
            return;
        }

        WorkspaceAutoSaveCapture? capture;
        try
        {
            capture = CaptureWorkspaceAutoSave(stored.Value, stored.Revision);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine(
                $"[ghostshell:autosave] Workspace capture failed: {exception}");
            return;
        }

        if (capture is null)
        {
            return;
        }

        var error = await _catalog.SaveWorkspaceWithLayoutsAsync(
            capture.Workspace,
            capture.WorkspaceRevision,
            capture.Layouts,
            CancellationToken.None);
        if (error is null)
        {
            await CleanUpOrphanedAutoSaveLayoutsAsync(capture.Workspace);
            return;
        }

        // A revision conflict means another writer got there first; the next
        // change captures against the fresh revision. Anything else is logged
        // rather than surfaced — autosave must not nag while the user works.
        if (error.Code != DefinitionStoreErrorCode.RevisionConflict)
        {
            Console.Error.WriteLine(
                $"[ghostshell:autosave] Workspace autosave failed: {error.Code}: {error.Message}");
        }
    }

    /// <summary>
    /// Captures the live tabs as workspace-only tab entries plus one auto-saved
    /// layout per tab. Returns null when the runtime is mid-mutation (placeholder
    /// or unavailable panels, dock tree out of step) or when nothing changed —
    /// which also breaks the save→refresh→save loop, since a save's own catalog
    /// refresh re-queues an identical capture.
    /// </summary>
    private WorkspaceAutoSaveCapture? CaptureWorkspaceAutoSave(
        WorkspaceDefinition storedDefinition,
        long storedRevision)
    {
        if (RuntimeWorkspace is not { Tabs.Count: > 0 } runtime)
        {
            return null;
        }

        var storedTabs = storedDefinition.Entries.OfType<WorkspaceEntry.Tab>().ToList();
        var storedLayouts = _catalog.Snapshot.Layouts
            .ToDictionary(item => item.Value.Id.Value, StringComparer.Ordinal);
        var usedStoredTabs = new HashSet<WorkspaceEntryId>();
        var layouts = new List<(LayoutDefinition Definition, long? ExpectedRevision)>();
        var entries = new List<WorkspaceEntry>();
        for (var index = 0; index < runtime.Tabs.Count; index++)
        {
            var tab = runtime.Tabs[index];
            if (IsLauncherTab(tab))
            {
                // A launcher tab is a question, not content: there is nothing
                // in it to describe. Deferring the whole pass for one froze the
                // definition for as long as it stayed open, and the workspace
                // then reopened from whatever had been saved before it appeared
                // — which reads as closed tabs coming back.
                continue;
            }

            // Dock documents are the durable slot identities: a restored panel
            // keeps its saved document id, so capturing by document keeps slot
            // ids stable across sessions. The document's context is the live
            // panel bound to that slot.
            var panelsBySlot = new Dictionary<string, RuntimePanelViewModel>(StringComparer.Ordinal);
            foreach (var region in DockLayoutProjection.CollectRegions(tab.DockLayout))
            {
                if (region.Document.Context is not RuntimePanelViewModel panel)
                {
                    // A document with no bound panel is an empty layout slot:
                    // it keeps its place in the dock geometry but gets no slot
                    // mapping.
                    continue;
                }

                if (PanelKindForAutoSave(panel) is null)
                {
                    // A placeholder or unavailable panel cannot be described
                    // durably; saving now would drop it from the definition.
                    // Defer the whole pass until the runtime settles.
                    return null;
                }

                panelsBySlot[region.Document.Id] = panel;
            }

            if (panelsBySlot.Count == 0 || panelsBySlot.Count != tab.Panels.Count)
            {
                return null;
            }

            var (grid, projectedSlots) = DockLayoutProjection.ProjectSlots(
                tab.DockLayout,
                id => panelsBySlot.TryGetValue(id, out var panel)
                    ? new LayoutMinimumSize(panel.LayoutMinimumWidth, panel.LayoutMinimumHeight)
                    : new LayoutMinimumSize(220, 140));
            var slots = projectedSlots
                .Where(slot => panelsBySlot.ContainsKey(slot.Id.Value))
                .ToArray();
            if (slots.Length != panelsBySlot.Count)
            {
                return null;
            }
            var layoutId = new LayoutId(
                $"{LayoutDefinition.AutoSaveIdPrefix}{storedDefinition.Id.Value}.tab-{index}");
            var layout = new LayoutDefinition(
                layoutId,
                LayoutDefinition.CurrentSchemaVersion,
                $"{tab.Title} (auto)",
                grid,
                slots,
                tab.SerializeDockLayout());
            layouts.Add((
                layout,
                storedLayouts.TryGetValue(layoutId.Value, out var storedLayout)
                    ? storedLayout.Revision
                    : null));

            var storedTab = storedTabs.FirstOrDefault(candidate =>
                !usedStoredTabs.Contains(candidate.Id)
                && string.Equals(candidate.Name, tab.Title, StringComparison.Ordinal));
            if (storedTab is not null)
            {
                usedStoredTabs.Add(storedTab.Id);
            }

            var usedStoredPanels = new HashSet<ScreenPanelId>();
            entries.Add(new WorkspaceEntry.Tab(
                storedTab?.Id ?? WorkspaceEntryId.New(),
                tab.Title,
                layoutId,
                slots
                    .Select(slot => CaptureAutoSavePanel(
                        panelsBySlot[slot.Id.Value],
                        slot.Id,
                        storedTab,
                        usedStoredPanels))
                    .ToArray()));
        }

        // Every tab is the launcher, so nothing durable is open and the
        // definition says so. Holding the previous entries back instead left the
        // workspace describing tabs the user had closed, and reopening it
        // brought them all back.

        // Connection and saved-screen references materialized into the live tabs
        // above; under autosave the definition is the live state, so the entry
        // list is replaced wholesale.
        var definition = new WorkspaceDefinition(
            storedDefinition.Id,
            WorkspaceDefinition.CurrentSchemaVersion,
            storedDefinition.Name,
            storedDefinition.Description,
            storedDefinition.Accent,
            entries,
            storedDefinition.AgentPolicyOverride,
            storedDefinition.Icon,
            autoSave: true,
            storedDefinition.Color,
            storedDefinition.AgentPanelPinned,
            storedDefinition.TerminalMultiplexingOverride);
        var unchanged = DefinitionPayloadEquals(definition, storedDefinition)
            && layouts.All(item =>
                storedLayouts.TryGetValue(item.Definition.Id.Value, out var existing)
                && DefinitionPayloadEquals(item.Definition, existing.Value));
        return unchanged
            ? null
            : new WorkspaceAutoSaveCapture(definition, storedRevision, layouts);
    }

    private static bool DefinitionPayloadEquals(object left, object right) =>
        left.GetType() == right.GetType()
        && string.Equals(
            System.Text.Json.JsonSerializer.Serialize(left, left.GetType()),
            System.Text.Json.JsonSerializer.Serialize(right, right.GetType()),
            StringComparison.Ordinal);

    /// <summary>
    /// The durable kind a live panel persists as, or null for panels that are
    /// not durable state. Unavailable panels keep their declared kind — their
    /// adapter is missing, not their identity — so autosave does not stall on
    /// them; <see cref="CaptureAutoSavePanel"/> falls back to the stored
    /// definition for the configuration they cannot express.
    /// </summary>
    private static ScreenPanelKind? PanelKindForAutoSave(RuntimePanelViewModel panel) =>
        panel is PanelPlaceholderViewModel
            ? null
            : panel.Kind switch
            {
                PanelKind.Terminal => ScreenPanelKind.Terminal,
                PanelKind.Browser => ScreenPanelKind.Browser,
                PanelKind.FileViewer => ScreenPanelKind.FileViewer,
                PanelKind.Statistics => ScreenPanelKind.Statistics,
                PanelKind.ProcessMonitor => ScreenPanelKind.ProcessMonitor,
                PanelKind.DatabaseViewer => ScreenPanelKind.DatabaseViewer,
                PanelKind.Docker => ScreenPanelKind.Docker,
                _ => null,
            };

    private static ScreenPanelDefinition CaptureAutoSavePanel(
        RuntimePanelViewModel panel,
        LayoutSlotId slotId,
        WorkspaceEntry.Tab? storedTab,
        HashSet<ScreenPanelId> usedStoredPanels)
    {
        var kind = PanelKindForAutoSave(panel)!.Value;
        ConnectionId? connectionId = panel switch
        {
            TerminalRuntimePanelViewModel terminal => terminal.ConnectionId,
            BrowserRuntimePanelViewModel browser => browser.ConnectionId,
            FileRuntimePanelViewModel file => file.ConnectionId,
            StatisticsRuntimePanelViewModel statistics => statistics.ConnectionId,
            ProcessMonitorRuntimePanelViewModel processes => processes.ConnectionId,
            DatabaseRuntimePanelViewModel database => database.TunnelConnectionId,
            DockerRuntimePanelViewModel docker => docker.ConnectionId,
            _ => null,
        };
        var stored = storedTab?.Panels.FirstOrDefault(candidate =>
            !usedStoredPanels.Contains(candidate.Id)
            && candidate.Kind == kind
            && (connectionId is null || candidate.ConnectionId == connectionId));
        if (stored is not null)
        {
            usedStoredPanels.Add(stored.Id);
        }

        string? location;
        if (panel is UnavailableRuntimePanelViewModel)
        {
            // The live panel cannot express its configuration, so the stored
            // definition keeps everything it already knows.
            connectionId ??= stored?.ConnectionId;
            location = stored?.Startup.Location;
        }
        else
        {
            location = panel switch
            {
                TerminalRuntimePanelViewModel terminal => terminal.RecoveryStartupLocation,
                BrowserRuntimePanelViewModel browser => browser.CurrentAddress.ToString(),
                DatabaseRuntimePanelViewModel database =>
                    database.RecoveryTarget ?? stored?.Startup.Location,
                _ => stored?.Startup.Location,
            };
        }
        FileProviderProfileId? fileProvider = kind != ScreenPanelKind.FileViewer
            ? null
            : panel is FileRuntimePanelViewModel fileViewer
                && (fileViewer.SelectedProfile?.Id ?? fileViewer.CurrentLocation?.ProviderProfileId)
                    is { } profileId
                ? new FileProviderProfileId(profileId)
                : stored?.FileProviderProfileId;
        // Startup commands cannot be read back from a live panel, so a matched
        // stored panel keeps the commands the user configured for this tab.
        return new ScreenPanelDefinition(
            stored?.Id ?? new ScreenPanelId(panel.Id.Value),
            slotId,
            kind,
            panel.Title,
            connectionId,
            new PanelStartupBehavior(
                location,
                stored?.Startup.Commands,
                stored?.Startup.DeliveryFailurePolicy
                    ?? StartupCommandDeliveryFailurePolicy.RetryWhileLive),
            fileProvider);
    }

    /// <summary>
    /// Deletes auto-saved layouts of this workspace that no live tab references
    /// any more — a closed tab leaves its captured layout behind otherwise. Best
    /// effort: a failure here only delays cleanup until the next save.
    /// </summary>
    private async Task CleanUpOrphanedAutoSaveLayoutsAsync(WorkspaceDefinition workspace)
    {
        var prefix = $"{LayoutDefinition.AutoSaveIdPrefix}{workspace.Id.Value}.";
        var referenced = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .Select(tab => tab.LayoutId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var layout in _catalog.Snapshot.Layouts
            .Where(item => item.Value.Id.Value.StartsWith(prefix, StringComparison.Ordinal)
                && !referenced.Contains(item.Value.Id.Value))
            .ToArray())
        {
            _ = await _catalog.DeleteAsync(
                layout.Value.Key,
                layout.Revision,
                CancellationToken.None);
        }
    }

    private void RefreshFileTransfers()
    {
        var snapshots = _fileTransferQueue.Transfers;
        var rows = snapshots.Select(snapshot =>
            new FileTransferItemViewModel(
                snapshot.Id,
                FileLocationPresentation.Display(snapshot.Request.Source),
                FileLocationPresentation.Display(snapshot.EffectiveDestination),
                snapshot.Request.Operation.ToString(),
                snapshot.State.ToString(),
                snapshot.Stage,
                FormatTransferProgress(snapshot),
                snapshot.Error?.Message,
                snapshot.Error is not null,
                snapshot.CanCancel,
                snapshot.CanRetry,
                snapshot.State is
                    FilePanelTransferState.Queued or FilePanelTransferState.Running,
                snapshot.TotalBytes is > 0,
                TransferPercent(snapshot),
                snapshot.QueuedAt))
            .ToArray();
        SynchronizeFileTransfers(rows);
        OnPropertyChanged(nameof(HasFileTransfers));
        OnPropertyChanged(nameof(HasNoFileTransfers));
        OnPropertyChanged(nameof(ActiveFileTransferCount));
        OnPropertyChanged(nameof(FailedFileTransferCount));
        OnPropertyChanged(nameof(FileTransferStatusText));

        foreach (var snapshot in snapshots.Where(snapshot =>
                     snapshot.State == FilePanelTransferState.Completed
                     && _refreshedFileTransfers.Add(snapshot.Id)))
        {
            _ = RefreshPanelsAfterTransferAsync(snapshot);
        }
    }

    private void SynchronizeFileTransfers(
        IReadOnlyList<FileTransferItemViewModel> latest)
    {
        var existingById = FileTransfers.ToDictionary(transfer => transfer.Id);
        for (var index = 0; index < latest.Count; index++)
        {
            var candidate = latest[index];
            if (!existingById.TryGetValue(candidate.Id, out var existing))
            {
                FileTransfers.Insert(index, candidate);
                continue;
            }

            existing.UpdateFrom(candidate);
            var currentIndex = FileTransfers.IndexOf(existing);
            if (currentIndex != index)
            {
                FileTransfers.Move(currentIndex, index);
            }
        }

        var liveIds = latest.Select(transfer => transfer.Id).ToHashSet();
        for (var index = FileTransfers.Count - 1; index >= 0; index--)
        {
            if (!liveIds.Contains(FileTransfers[index].Id))
            {
                FileTransfers.RemoveAt(index);
            }
        }
    }

    private async Task RefreshPanelsAfterTransferAsync(
        FilePanelTransferSnapshot transfer)
    {
        if (RuntimeWorkspace is null || _shutdownStarted)
        {
            return;
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal)
        {
            transfer.Request.Source.ProviderProfileId,
            transfer.EffectiveDestination.ProviderProfileId,
        };
        var panels = RuntimeWorkspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<FileRuntimePanelViewModel>()
            .Where(panel => panel.SelectedProfile is not null
                && profileIds.Contains(panel.SelectedProfile.Id))
            .ToArray();

        foreach (var panel in panels)
        {
            try
            {
                await panel
                    .RefreshAsync(_runtimeGraphLifetime.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
                when (_runtimeGraphLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private IFileTransferQueueClient ResolveFileTransferQueue(FilePanelTransferId id) =>
        RuntimeWorkspace?.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<FileRuntimePanelViewModel>()
            .Select(panel => panel.HostedClient)
            .OfType<IFileTransferQueueClient>()
            .FirstOrDefault(queue => queue.Transfers.Any(transfer => transfer.Id == id))
        ?? _fileTransferQueue;

    public async Task<bool> OpenRecentSessionAsync(
        RecentSessionHistoryItemViewModel recentSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recentSession);
        if (!CanOpenDefinition(recentSession.SourceDefinition))
        {
            SetError(
                "The current saved definition for that recent session is missing or unavailable on this platform.");
            return false;
        }

        // Launch, not open: reopening something joins the workspace you are in
        // rather than standing up one of its own beside it. Opening has never
        // been allowed to replace what is already running, and arriving
        // detached from the workspace the request came from is the same
        // surprise wearing a different coat.
        var source = recentSession.SourceDefinition;
        if (source.Kind == ConnectionProfile.Kind)
        {
            return await LaunchConnectionAsync(
                new ConnectionId(source.Value),
                cancellationToken);
        }

        if (source.Kind == ScreenDefinition.Kind)
        {
            return await LaunchScreenAsync(new ScreenId(source.Value), cancellationToken);
        }

        if (source.Kind == WorkspaceDefinition.Kind)
        {
            return await OpenWorkspaceAsync(new WorkspaceId(source.Value), cancellationToken);
        }

        SetError("That recent-session definition type cannot be reopened here.");
        return false;
    }

    public async Task<bool> ClearRecentSessionsAsync(CancellationToken cancellationToken)
    {
        if (_recentSessionHistory is null)
        {
            return false;
        }

        var cutoff = _recentSessionHistory.CaptureClearCutoff();
        return await ClearRecentSessionsAsync(cutoff, cancellationToken);
    }

    public RecentSessionClearCutoff CaptureRecentSessionClearCutoff() =>
        _recentSessionHistory?.CaptureClearCutoff()
        ?? new RecentSessionClearCutoff(_timeProvider.GetUtcNow());

    public async Task<bool> ClearRecentSessionsAsync(
        RecentSessionClearCutoff cutoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cutoff);
        if (_recentSessionHistory is null || IsHistoryLoading || IsHistoryMutating)
        {
            return false;
        }

        IsHistoryMutating = true;
        var cleared = false;
        try
        {
            var operation = QueueHistoryOperation(async token =>
            {
                var result = await _recentSessionHistory.ClearThroughAsync(cutoff, token);
                if (!result.IsSuccess)
                {
                    ApplyRecentSessionPersistenceFailure(result.Error!);
                    return;
                }

                cleared = true;
                await RefreshRecentSessionsCoreAsync(token);
            });

            await operation.WaitAsync(cancellationToken);
            return cleared;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsHistoryMutating = false;
        }
    }

    public async Task<bool> ResetUnreadableRecentSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (_recentSessionHistory is null
            || !HasUnreadableRecentSessionHistory
            || IsHistoryLoading
            || IsHistoryMutating)
        {
            return false;
        }

        IsHistoryMutating = true;
        var reset = false;
        try
        {
            var operation = QueueHistoryOperation(async token =>
            {
                var result = await _recentSessionHistory.ClearAllAsync(token);
                if (!result.IsSuccess)
                {
                    ApplyRecentSessionPersistenceFailure(result.Error!);
                    return;
                }

                reset = true;
                await RefreshRecentSessionsCoreAsync(token);
            });

            await operation.WaitAsync(cancellationToken);
            return reset && !HasRecentSessionFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsHistoryMutating = false;
        }
    }

    public async Task<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
        SaveHistoryRetentionAsync(CancellationToken cancellationToken)
    {
        if (_recentSessionHistory is null
            || _storedHistoryRetention is not { } stored
            || SelectedHistoryRetentionOption is not { } selected)
        {
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
                new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.StorageUnavailable,
                    "Recent-session retention settings are unavailable."));
        }

        if (selected.Policy == stored.Policy)
        {
            HasPendingHistoryRetentionChange = false;
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(
                new RecentSessionRetentionUpdateResult(stored, 0));
        }

        if (IsHistoryLoading || IsHistoryMutating)
        {
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
                new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.Conflict,
                    "Another session-history change is already running."));
        }

        IsHistoryMutating = true;
        try
        {
            RecentSessionStoreResult<RecentSessionRetentionUpdateResult>? saved = null;
            var operation = QueueHistoryOperation(async token =>
            {
                saved = await _recentSessionHistory.UpdateRetentionAsync(
                    selected.Policy,
                    stored.Revision,
                    token);
                if (!saved.IsSuccess)
                {
                    HistoryRetentionStatus =
                        $"History privacy settings could not be saved ({saved.Error!.Code}).";
                    if (saved.Error.Code == RecentSessionStoreErrorCode.Conflict)
                    {
                        await RefreshRecentSessionsCoreAsync(
                            token,
                            replaceRetentionSelection: true);
                        if (_storedHistoryRetention is { Revision: var currentRevision }
                            && currentRevision != stored.Revision)
                        {
                            HistoryRetentionStatus =
                                "History privacy settings changed elsewhere; the current policy was reloaded.";
                        }
                    }
                    else
                    {
                        ApplyRecentSessionPersistenceFailure(saved.Error);
                    }

                    return;
                }

                HasPendingHistoryRetentionChange = false;
                ApplyStoredHistoryRetention(
                    saved.Value!.StoredPolicy,
                    replaceSelection: true);
                var completionStatus = saved.Value.PrunedSessionCount == 0
                    ? "History privacy settings saved."
                    : $"History privacy settings saved; {CountLabel(saved.Value.PrunedSessionCount, "retained record")} removed.";
                await RefreshRecentSessionsCoreAsync(token);
                if (_storedHistoryRetention?.Revision == saved.Value.StoredPolicy.Revision)
                {
                    HistoryRetentionStatus = completionStatus;
                }
            });

            await operation.WaitAsync(cancellationToken);
            return saved ?? RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
                new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.Cancelled,
                    "Saving recent-session retention was cancelled."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
                new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.Cancelled,
                    "Saving recent-session retention was cancelled."));
        }
        finally
        {
            IsHistoryMutating = false;
        }
    }

    public async Task<ApplicationRunResult<Unit>> FlushRecentSessionHistoryAsync(
        CancellationToken cancellationToken)
    {
        Task pending;
        lock (_historyGate)
        {
            pending = _historyOperations;
        }

        try
        {
            await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.Cancelled,
                "Waiting for recent-session persistence was cancelled."));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.StorageFailure,
                "Recent-session persistence could not be drained."));
        }

        RecentSessionStoreError? error;
        lock (_historyGate)
        {
            error = _historyDrainError;
        }

        return error is null
            ? ApplicationRunResult<Unit>.Success(Unit.Value)
            : ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                error.Code == RecentSessionStoreErrorCode.StorageUnavailable
                    ? ApplicationRunErrorCode.StorageUnavailable
                    : ApplicationRunErrorCode.StorageFailure,
                $"Recent-session metadata could not be persisted safely: {error.Message}"));
    }

    /// <summary>
    /// Re-derives what the rails show about running workspaces: which are open,
    /// which one is in front, and which are asking to be noticed.
    ///
    /// Derived rather than pushed, because the rail lists saved definitions
    /// while "open" is a fact about runtime instances. The join is the
    /// definition key, the same one <see cref="FindOpenWorkspace"/> uses.
    /// </summary>
    private void RefreshWorkspaceRuntimeFlags()
    {
        foreach (var item in Workspaces)
        {
            var runtime = FindOpenWorkspace(
                new DefinitionKey(WorkspaceDefinition.Kind, item.Id.Value));
            item.IsOpen = runtime is not null;
            item.IsInFront = runtime is not null && ReferenceEquals(runtime, RuntimeWorkspace);
            item.HasAttention = runtime?.HasAttention == true;
        }

        OnPropertyChanged(nameof(HasWorkspaceAttention));
    }

    /// <summary>
    /// Whether anything in any workspace wants attention.
    ///
    /// The rail marks the workspace it happened in, but the rail can be turned
    /// off — and then nothing said so at all. The menu that lists workspaces
    /// carries the mark instead, so a workspace calling from behind a hidden
    /// rail is still visible from the chrome.
    /// </summary>
    public bool HasWorkspaceAttention =>
        !ShowWorkspacesPanel && Workspaces.Any(item => item.HasAttention);

    /// <summary>
    /// The running instance of a saved workspace, if it is running. The rail
    /// lists definitions and the host speaks in instances, so closing from the
    /// rail has to cross that boundary somewhere; here is the only place that
    /// already knows how.
    /// </summary>
    public WorkspaceInstanceId? OpenWorkspaceInstance(WorkspaceId workspaceId) =>
        FindOpenWorkspace(new DefinitionKey(WorkspaceDefinition.Kind, workspaceId.Value))?.Id;

    /// <summary>
    /// Takes a workspace out of the shell once the host has ended its sessions,
    /// falling back to the one you were in before it. Closing the last one
    /// leaves nothing to fall back to, so the launcher takes over.
    /// </summary>
    public void RemoveRuntimeWorkspace(WorkspaceInstanceId workspaceId)
    {
        if (_openWorkspaces.FirstOrDefault(runtime => runtime.Id == workspaceId)
            is not { } runtime)
        {
            return;
        }

        CloseRuntimeWorkspace(runtime);
        if (RuntimeWorkspace is null)
        {
            Route = ShellRoute.Workspace;
        }
    }

    private RuntimeWorkspaceViewModel? FindOpenWorkspace(DefinitionKey definition) =>
        _openWorkspaces.FirstOrDefault(runtime =>
            _runtimeSources.TryGetValue(runtime.Id, out var source)
            && source.SourceDefinition == definition);

    /// <summary>
    /// The one way a runtime workspace goes on screen.
    ///
    /// <paramref name="sourceDefinition"/> is null for the workspaces that have
    /// no saved definition behind them — a local browser, a monitor, an ad-hoc
    /// database. Those still belong in the open set: membership is what stops
    /// the next switch from disposing their panels, and it was the absence of
    /// it that made "open a browser, then click a workspace tile" throw the
    /// browser away.
    /// </summary>
    private void ActivateRuntimeWorkspace(
        RuntimeWorkspaceViewModel runtime,
        DefinitionKey? sourceDefinition,
        string durableTitle)
    {
        if (sourceDefinition is { } definition)
        {
            _runtimeSources[runtime.Id] = new RuntimeHistorySource(definition, durableTitle);
        }

        BringToFrontOfOpenSet(runtime);
        RuntimeWorkspace = runtime;
        Notifications.Watch(runtime);
        StartAcceptedRuntimePanels(runtime);
        TrackRecentSessions(runtime.Tabs.SelectMany(tab => tab.Panels));
        StartRuntimeGraphWatch(runtime);
        RefreshWorkspaceRuntimeFlags();
        Notifications.MarkVisibleSeen();
    }

    /// <summary>
    /// Brings an already-open workspace back to the front. Nothing is started
    /// or restored: its sessions never stopped.
    /// </summary>
    private void ReactivateRuntimeWorkspace(RuntimeWorkspaceViewModel runtime)
    {
        var activation = new ActivationTrace();
        _activation = activation;
        try
        {
            BringToFrontOfOpenSet(runtime);
            RuntimeWorkspace = runtime;
            StartRuntimeGraphWatch(runtime);
            activation.Mark("graph watch");
            RefreshWorkspaceRuntimeFlags();
            activation.Mark("rail flags");
            Notifications.MarkVisibleSeen();
            activation.Mark("seen marks");
        }
        finally
        {
            _activation = null;
        }

        if (activation.TryDescribe(ActivationBudgetMilliseconds, out var description))
        {
            Console.Error.WriteLine($"[ghostshell:perf] activation — {description}");
        }
    }

    /// <summary>
    /// Roughly two frames. Under it, bringing a workspace forward is not felt.
    /// </summary>
    private const long ActivationBudgetMilliseconds = 32;

    private ActivationTrace? _activation;

    /// <summary>
    /// Times the steps of bringing a workspace forward, so a slow one says
    /// which step it was.
    ///
    /// All of this runs on the thread that draws, and the phase timing around
    /// it could only say "the view model" — which is most of a third of a
    /// second and a dozen different things. The steps are marked where they
    /// happen, including inside the property setter that does most of them,
    /// because that is the only place their boundaries exist.
    /// </summary>
    private sealed class ActivationTrace
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(string Step, long Milliseconds)> _steps = [];
        private long _previous;

        public void Mark(string step)
        {
            var elapsed = _clock.ElapsedMilliseconds;
            _steps.Add((step, elapsed - _previous));
            _previous = elapsed;
        }

        public bool TryDescribe(long budgetMilliseconds, out string description)
        {
            _clock.Stop();
            var total = _clock.ElapsedMilliseconds;
            if (total < budgetMilliseconds)
            {
                description = string.Empty;
                return false;
            }

            description = $"{total} ms: "
                + string.Join(
                    ", ",
                    _steps.Select(step => $"{step.Step} {step.Milliseconds} ms"));
            return true;
        }
    }

    /// <summary>
    /// Keeps the open set in the order the workspaces were last looked at,
    /// least recent first. That ordering is what makes "the one before this
    /// one" answerable when the workspace in front is closed — appending on
    /// first open only ever gave the oldest, which is rarely where you were.
    /// </summary>
    private void BringToFrontOfOpenSet(RuntimeWorkspaceViewModel runtime)
    {
        var index = _openWorkspaces.IndexOf(runtime);
        if (index < 0)
        {
            _openWorkspaces.Add(runtime);
            return;
        }

        if (index != _openWorkspaces.Count - 1)
        {
            _openWorkspaces.Move(index, _openWorkspaces.Count - 1);
        }
    }

    /// <summary>
    /// Closes the workspace in front, and shows the launcher only when it was
    /// the last one. Another open workspace is somewhere to go back to.
    /// </summary>
    private void CloseActiveRuntimeWorkspace()
    {
        if (RuntimeWorkspace is { } runtime)
        {
            CloseRuntimeWorkspace(runtime);
        }

        if (RuntimeWorkspace is null)
        {
            Route = ShellRoute.Workspace;
        }
    }

    /// <summary>
    /// Closes a workspace for good: it leaves the open set, its panels are
    /// disposed, and if it was the one in front another takes its place.
    /// </summary>
    private void CloseRuntimeWorkspace(RuntimeWorkspaceViewModel runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var wasActive = ReferenceEquals(RuntimeWorkspace, runtime);
        _openWorkspaces.Remove(runtime);
        _runtimeSources.Remove(runtime.Id);
        _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
        Notifications.Forget(runtime);
        if (wasActive)
        {
            // The setter disposes what is no longer in the open set, so the
            // removal above is what makes this a close rather than a switch.
            RuntimeWorkspace = _openWorkspaces.LastOrDefault();
            if (RuntimeWorkspace is { } next)
            {
                ReactivateRuntimeWorkspace(next);
            }
            else
            {
                // Nothing left to fall back to, and the rail still has the tile
                // it was closed from marked as running.
                RefreshWorkspaceRuntimeFlags();
            }

            return;
        }

        StopTrackingRecovery(runtime);
        StopTrackingAgentTerminalSelection(runtime);
        runtime.DisposePanels();
        RefreshWorkspaceRuntimeFlags();
    }

    private void DisposeRuntimeWorkspaceUnlessOwned(
        RuntimeWorkspaceViewModel runtime)
    {
        if (!ReferenceEquals(RuntimeWorkspace, runtime))
        {
            _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            runtime.DisposePanels();
        }
    }

    private void StartRuntimeGraphWatch(RuntimeWorkspaceViewModel runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeGraphLifetime.Token);
        lock (_shutdownGate)
        {
            if (_shutdownStarted)
            {
                cancellation.Dispose();
                return;
            }

            _runtimeGraphWatchCancellation = cancellation;
            _runtimeGraphWatchTasks.Add(WatchRuntimeWorkspaceGraphAsync(
                runtime,
                runtime.HostSequence,
                cancellation.Token));
        }
    }

    private void StopRuntimeGraphWatch()
    {
        CancellationTokenSource? cancellation;
        lock (_shutdownGate)
        {
            cancellation = _runtimeGraphWatchCancellation;
            _runtimeGraphWatchCancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task WatchRuntimeWorkspaceGraphAsync(
        RuntimeWorkspaceViewModel runtime,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        try
        {
            var cursor = afterSequence;
            while (!cancellationToken.IsCancellationRequested)
            {
                var restartAfterResynchronization = false;
                await foreach (var item in SessionClient.WatchWorkspaceGraphAsync(
                    new WatchWorkspaceGraphRequest(runtime.Id, cursor),
                    OperationContext.ForHuman(ClientId),
                    cancellationToken).ConfigureAwait(false))
                {
                    await _runtimeGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(RuntimeWorkspace, runtime))
                        {
                            return;
                        }

                        var accepted = false;
                        await _uiThreadDispatcher.InvokeAsync(
                            () => accepted = ApplyRuntimeWorkspaceGraphStreamItem(runtime, item),
                            cancellationToken);
                        if (!accepted)
                        {
                            return;
                        }

                        cursor = runtime.HostSequence;
                    }
                    finally
                    {
                        _runtimeGraphGate.Release();
                    }

                    if (item is WorkspaceGraphStreamItem.ResynchronizationRequired)
                    {
                        restartAfterResynchronization = true;
                        break;
                    }
                }

                if (!restartAfterResynchronization)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (NotSupportedException)
        {
            // Compatibility clients can omit workspace watches. Production desktop clients
            // implement the stream; mutation receipts still keep legacy clients coherent.
        }
        catch (Exception)
        {
            try
            {
                await _uiThreadDispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(RuntimeWorkspace, runtime))
                    {
                        SetError("Live workspace updates are temporarily unavailable.");
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The dispatcher can stop between the stream failure and this best-effort
                // presentation update. There is no remaining UI surface to notify.
            }
        }
    }

    private bool ApplyRuntimeWorkspaceGraphStreamItem(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceGraphStreamItem item)
    {
        if (!ReferenceEquals(RuntimeWorkspace, runtime))
        {
            return false;
        }

        switch (item)
        {
            case WorkspaceGraphStreamItem.Event { Value: var workspaceEvent }
                when workspaceEvent.Sequence <= runtime.HostSequence:
                return true;
            case WorkspaceGraphStreamItem.Event
            {
                Value.Kind: WorkspaceGraphEventKind.Removed,
                Value: var workspaceEvent,
            }:
                if (workspaceEvent.WindowId != WindowId
                    || workspaceEvent.WorkspaceId != runtime.Id
                    || workspaceEvent.Revision < runtime.HostRevision)
                {
                    SetError("The session host returned an invalid workspace removal event.");
                    return false;
                }

                CloseRuntimeWorkspace(runtime);
                CloseOverlay();
                if (RuntimeWorkspace is null)
                {
                    Route = ShellRoute.Workspace;
                }

                return true;
            case WorkspaceGraphStreamItem.Event { Value: var workspaceEvent }:
                return TryApplyRuntimeWorkspaceProjection(
                    runtime,
                    workspaceEvent.WindowId,
                    workspaceEvent.Workspace,
                    workspaceEvent.Revision,
                    workspaceEvent.Sequence,
                    "workspace event");
            case WorkspaceGraphStreamItem.ResynchronizationRequired
            {
                Snapshot: var snapshot,
                ResumeAfterSequence: var resumeAfterSequence,
            }:
                if (resumeAfterSequence != snapshot.LastSequence)
                {
                    SetError("The session host returned an invalid workspace resynchronization cursor.");
                    return false;
                }

                if (resumeAfterSequence <= runtime.HostSequence)
                {
                    return true;
                }

                return TryApplyRuntimeWorkspaceProjection(
                    runtime,
                    snapshot.WindowId,
                    snapshot.Workspace,
                    snapshot.Revision,
                    resumeAfterSequence,
                    "workspace resynchronization");
            default:
                throw new ArgumentOutOfRangeException(nameof(item));
        }
    }

    private async Task<bool> RegisterRuntimeWorkspaceAsync(
        RuntimeWorkspaceViewModel runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            var proposal = CaptureRuntimeWorkspaceGraph(runtime);
            HostResult<WorkspaceGraphSnapshot> result;
            try
            {
                result = await SessionClient.RegisterWorkspaceGraphAsync(
                    new RegisterWorkspaceGraphRequest(
                        WindowId,
                        proposal),
                    OperationContext.ForHuman(
                        ClientId,
                        idempotencyKey: IdempotencyKey.New()),
                    linkedCancellation.Token);
            }
            catch (Exception exception) when (
                IsAmbiguousWorkspaceGraphReceiptFailure(exception)
                && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                var authoritative = await TryQueryWorkspaceGraphForReconciliationAsync(
                    runtime.Id);
                if (authoritative
                        is not HostResult<WorkspaceGraphSnapshot>.Success reconciledSuccess
                    || !IsExpectedWorkspaceGraphReceipt(
                        reconciledSuccess,
                        proposal,
                        currentRevision: 0,
                        currentSequence: 0))
                {
                    throw;
                }

                result = reconciledSuccess;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                SetError("The runtime workspace graph could not be registered.");
                return false;
            }

            if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
            {
                SetError(
                    $"The session host rejected workspace registration " +
                    $"({failure.Error.StableCode}): {failure.Error.Message}");
                return false;
            }

            var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
            return TryApplyRegisteredRuntimeWorkspace(runtime, success);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    private async Task<bool> ReplaceRuntimeWorkspaceGraphAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?> buildProposal,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(buildProposal);
        ArgumentNullException.ThrowIfNull(commit);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, runtime))
            {
                return false;
            }

            WorkspaceInstance? proposal;
            try
            {
                proposal = buildProposal(runtime);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                SetError($"The runtime workspace changed before {operation} could start.");
                return false;
            }

            if (proposal is null)
            {
                SetError($"The runtime workspace changed before {operation} could start.");
                return false;
            }

            return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                runtime,
                proposal,
                operation,
                commit,
                RuntimeGraphStaleProposalHandling.RefreshAndRetry,
                linkedCancellation.Token,
                buildProposal);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    // The caller owns _runtimeGraphGate. Keeping proposal submission and the
    // observable commit in one critical section prevents optimistic UI order.
    private async Task<bool> ReplaceRuntimeWorkspaceGraphUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceInstance proposal,
        string operation,
        Action commit,
        RuntimeGraphStaleProposalHandling staleProposalHandling,
        CancellationToken cancellationToken,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?>? rebuildProposal = null)
    {
        if (!ReferenceEquals(RuntimeWorkspace, runtime))
        {
            return false;
        }

        HostResult<WorkspaceGraphSnapshot>? result = null;
        var reconciledAfterAmbiguousReceipt = false;
        var idempotencyKey = IdempotencyKey.New();
        var attemptCount = staleProposalHandling
            == RuntimeGraphStaleProposalHandling.RefreshAndRetry
            ? WorkspaceMutationAttemptCount
            : 1;
        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            try
            {
                var request = new RegisterWorkspaceGraphRequest(WindowId, proposal);
                var attemptResult = await SessionClient.RegisterWorkspaceGraphAsync(
                    request,
                    OperationContext.ForHuman(
                        ClientId,
                        runtime.HostRevision,
                        idempotencyKey),
                    cancellationToken);
                result = attemptResult;
                if (staleProposalHandling == RuntimeGraphStaleProposalHandling.RefreshAndRetry
                    && await TryRefreshRevisionConflictAsync(
                        runtime,
                        attemptResult,
                        attempt,
                        cancellationToken))
                {
                    if (rebuildProposal is null)
                    {
                        SetError(
                            $"The runtime workspace changed before {operation} could retry.");
                        return false;
                    }

                    var rebuiltProposal = rebuildProposal(runtime);
                    if (rebuiltProposal is null)
                    {
                        SetError(
                            $"The runtime workspace changed before {operation} could retry.");
                        return false;
                    }

                    proposal = rebuiltProposal;
                    idempotencyKey = IdempotencyKey.New();
                    continue;
                }
            }
            catch (Exception exception) when (
                IsAmbiguousWorkspaceGraphReceiptFailure(exception)
                && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                var reconciled = await TryReconcileWorkspaceGraphMutationAsync(
                    runtime,
                    proposal);
                if (reconciled is null)
                {
                    throw;
                }

                result = reconciled;
                reconciledAfterAmbiguousReceipt = true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                SetError($"The runtime workspace could not apply {operation}.");
                return false;
            }

            break;
        }

        if (result is null)
        {
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            SetError(
                $"The session host rejected {operation} " +
                $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        var receiptIsExpected = reconciledAfterAmbiguousReceipt
            ? IsExpectedReconciledWorkspaceGraphReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence)
            : IsExpectedWorkspaceGraphReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence);
        if (!receiptIsExpected)
        {
            SetError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        commit();
        var applied = reconciledAfterAmbiguousReceipt
            ? TryApplyRuntimeWorkspaceProjection(
                runtime,
                success.Value.WindowId,
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence,
                $"{operation} reconciliation")
            : TryApplyRegisteredRuntimeWorkspace(runtime, success);
        if (!applied)
        {
            throw new InvalidOperationException(
                $"The host-approved {operation} could not be applied to the runtime view.");
        }

        if (!reconciledAfterAmbiguousReceipt)
        {
            QueueRuntimeRecoverySnapshot();
        }

        return true;
    }

    private async ValueTask<HostResult<WorkspaceGraphSnapshot>.Success?>
        TryReconcileWorkspaceGraphMutationAsync(
            RuntimeWorkspaceViewModel runtime,
            WorkspaceInstance proposal)
    {
        var result = await TryQueryWorkspaceGraphForReconciliationAsync(
            runtime.Id);
        return result is HostResult<WorkspaceGraphSnapshot>.Success success
            && IsExpectedReconciledWorkspaceGraphReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence)
                ? success
                : null;
    }

    private async ValueTask<HostResult<WorkspaceGraphSnapshot>?>
        TryQueryWorkspaceGraphForReconciliationAsync(
            WorkspaceInstanceId workspaceId)
    {
        using var timeoutCancellation = new CancellationTokenSource(
            WorkspaceGraphReceiptReconciliationTimeout,
            _timeProvider);
        using var reconciliationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeGraphLifetime.Token,
                timeoutCancellation.Token);
        try
        {
            return await SessionClient.GetWorkspaceGraphAsync(
                workspaceId,
                OperationContext.ForHuman(ClientId),
                reconciliationCancellation.Token);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or NotSupportedException
                or OperationCanceledException
                or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsAmbiguousWorkspaceGraphReceiptFailure(
        Exception exception) =>
        exception is OperationCanceledException or IOException or TimeoutException;

    // The caller owns _runtimeGraphGate and has already revalidated the live
    // workspace and the operation-specific intent.
    private async Task<bool> UnregisterRuntimeWorkspaceUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(commit);

        if (!ReferenceEquals(RuntimeWorkspace, runtime))
        {
            return false;
        }

        HostResult<Unit>? result = null;
        var reconciledRemoval = false;
        var request = new UnregisterWorkspaceGraphRequest(WindowId, runtime.Id);
        var idempotencyKey = IdempotencyKey.New();
        for (var attempt = 0; attempt < WorkspaceMutationAttemptCount; attempt++)
        {
            try
            {
                var attemptResult = await SessionClient.UnregisterWorkspaceGraphAsync(
                    request,
                    OperationContext.ForHuman(
                        ClientId,
                        runtime.HostRevision,
                        idempotencyKey),
                    cancellationToken);
                result = attemptResult;
                if (await TryRefreshRevisionConflictAsync(
                    runtime,
                    attemptResult,
                    attempt,
                    cancellationToken))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                IsAmbiguousWorkspaceGraphReceiptFailure(exception)
                && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                var authoritative = await TryQueryWorkspaceGraphForReconciliationAsync(
                    runtime.Id);
                if (authoritative is not HostResult<WorkspaceGraphSnapshot>.Failure
                    {
                        Error.Code: HostErrorCode.NotFound,
                    })
                {
                    throw;
                }

                reconciledRemoval = true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                SetError($"The runtime workspace could not apply {operation}.");
                return false;
            }

            break;
        }

        if (reconciledRemoval)
        {
            commit();
            return true;
        }

        if (result is null)
        {
            return false;
        }

        if (result is HostResult<Unit>.Failure failure)
        {
            SetError(
                $"The session host rejected {operation} " +
                $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<Unit>.Success)result;
        if (success.ResultingRevision <= runtime.HostRevision)
        {
            SetError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        commit();
        return true;
    }

    private bool TryApplyRegisteredRuntimeWorkspace(
        RuntimeWorkspaceViewModel runtime,
        HostResult<WorkspaceGraphSnapshot>.Success success)
    {
        if (!IsExpectedWorkspaceGraphReceipt(
                success,
                CaptureRuntimeWorkspaceGraph(runtime),
                runtime.HostRevision,
                runtime.HostSequence))
        {
            SetError("The session host returned an invalid workspace registration receipt.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence);
            return true;
        }
        catch (InvalidOperationException)
        {
            SetError("The session host returned a different runtime workspace graph.");
            return false;
        }
    }

    private bool TryApplyRuntimeWorkspaceResult(
        RuntimeWorkspaceViewModel expectedWorkspace,
        HostResult<WorkspaceGraphSnapshot> result,
        string operation,
        Func<WorkspaceInstance, bool> requestedFocusMatches)
    {
        if (!ReferenceEquals(RuntimeWorkspace, expectedWorkspace))
        {
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            SetError(
                $"The session host rejected {operation} " +
                $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        var currentProjection = CaptureRuntimeWorkspaceGraph(expectedWorkspace);
        var sameCursor =
            success.Value.Revision == expectedWorkspace.HostRevision
            && success.Value.LastSequence == expectedWorkspace.HostSequence;
        var advancedCursor =
            success.Value.Revision > expectedWorkspace.HostRevision
            && success.Value.LastSequence > expectedWorkspace.HostSequence;
        if (success.Value.WindowId != WindowId
            || success.Value.Workspace.Id != expectedWorkspace.Id
            || success.ResultingRevision != success.Value.Revision
            || !requestedFocusMatches(success.Value.Workspace)
            || !(advancedCursor
                || sameCursor && requestedFocusMatches(currentProjection))
            || !WorkspaceTopologyMatches(
                currentProjection,
                success.Value.Workspace))
        {
            SetError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        try
        {
            expectedWorkspace.ApplyHostProjection(
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence);
        }
        catch (InvalidOperationException)
        {
            SetError("The session host returned a different runtime workspace graph.");
            return false;
        }

        QueueRuntimeRecoverySnapshot();
        return true;
    }

    private bool TryApplyRuntimeWorkspaceProjection(
        RuntimeWorkspaceViewModel runtime,
        WindowInstanceId windowId,
        WorkspaceInstance projection,
        long revision,
        long sequence,
        string source)
    {
        if (!ReferenceEquals(RuntimeWorkspace, runtime))
        {
            return false;
        }

        if (windowId != WindowId
            || projection.Id != runtime.Id
            || revision < runtime.HostRevision
            || sequence < runtime.HostSequence
            || !WorkspaceTopologyMatches(
                CaptureRuntimeWorkspaceGraph(runtime),
                projection))
        {
            SetError($"The session host returned an invalid {source}.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(projection, revision, sequence);
        }
        catch (InvalidOperationException)
        {
            SetError($"The session host returned a different {source} graph.");
            return false;
        }

        // Every activation the host confirms — a tab, a panel, a drag that moved
        // one — lands here, which makes this the one place that reliably knows
        // what the user is now looking at.
        Notifications.MarkVisibleSeen();
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    private async ValueTask<bool> RefreshRuntimeWorkspaceProjectionAsync(
        RuntimeWorkspaceViewModel runtime,
        CancellationToken cancellationToken)
    {
        HostResult<WorkspaceGraphSnapshot> result;
        try
        {
            result = await SessionClient.GetWorkspaceGraphAsync(
                runtime.Id,
                OperationContext.ForHuman(ClientId),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or NotSupportedException)
        {
            SetError("The runtime workspace could not be refreshed.");
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            SetError(
                $"The session host could not refresh the workspace " +
                $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        if (success.ResultingRevision != success.Value.Revision)
        {
            SetError("The session host returned an invalid workspace refresh receipt.");
            return false;
        }

        return TryApplyRuntimeWorkspaceProjection(
            runtime,
            success.Value.WindowId,
            success.Value.Workspace,
            success.Value.Revision,
            success.Value.LastSequence,
            "workspace refresh");
    }

    private async ValueTask<bool> TryRefreshRevisionConflictAsync<T>(
        RuntimeWorkspaceViewModel runtime,
        HostResult<T> result,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt != 0
            || result is not HostResult<T>.Failure
            {
                Error.Code: HostErrorCode.RevisionConflict,
            } failure
            || failure.CurrentRevision <= runtime.HostRevision)
        {
            return false;
        }

        return await RefreshRuntimeWorkspaceProjectionAsync(runtime, cancellationToken);
    }

    private bool IsExpectedWorkspaceGraphReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == WindowId
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && WorkspaceIntentMatches(proposal, success.Value.Workspace);

    private bool IsExpectedReconciledWorkspaceGraphReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == WindowId
        && success.Value.Workspace.Id == proposal.Id
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && WorkspaceTopologyMatches(proposal, success.Value.Workspace);

    private static bool WorkspaceIntentMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual) =>
        expected.ActiveTabId == actual.ActiveTabId
        && expected.Tabs.Zip(actual.Tabs).All(pair =>
            pair.First.ActivePanelId == pair.Second.ActivePanelId)
        && WorkspaceTopologyMatches(expected, actual);

    private static bool WorkspaceTopologyMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual)
    {
        if (expected.Id != actual.Id
            || !string.Equals(expected.Title, actual.Title, StringComparison.Ordinal)
            || expected.Tabs.Count != actual.Tabs.Count)
        {
            return false;
        }

        for (var tabIndex = 0; tabIndex < expected.Tabs.Count; tabIndex++)
        {
            var expectedTab = expected.Tabs[tabIndex];
            var actualTab = actual.Tabs[tabIndex];
            if (expectedTab.Id != actualTab.Id
                || !string.Equals(expectedTab.Title, actualTab.Title, StringComparison.Ordinal)
                || expectedTab.Panels.Count != actualTab.Panels.Count)
            {
                return false;
            }

            for (var panelIndex = 0; panelIndex < expectedTab.Panels.Count; panelIndex++)
            {
                var expectedPanel = expectedTab.Panels[panelIndex];
                var actualPanel = actualTab.Panels[panelIndex];
                if (expectedPanel.Id != actualPanel.Id
                    || expectedPanel.Kind != actualPanel.Kind
                    || !string.Equals(
                        expectedPanel.Title,
                        actualPanel.Title,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static WorkspaceInstance CaptureRuntimeWorkspaceGraph(
        RuntimeWorkspaceViewModel workspace)
    {
        var activeTab = workspace.ActiveTab
            ?? throw new InvalidOperationException(
                "A runtime workspace must have an active tab before registration.");
        return new WorkspaceInstance(
            workspace.Id,
            workspace.Name,
            workspace.Tabs.Select(CaptureRuntimeTab),
            activeTab.Id);
    }

    /// <summary>
    /// The tab as the session host knows it.
    ///
    /// A placed but unanswered cell is a panel like any other. It carries no
    /// session — a panel's session has always been optional — but it occupies the
    /// layout, it can be selected, and it can be the only thing in a tab, which is
    /// what a tab opened straight onto the launcher is.
    ///
    /// It was once left out, so the host would not be asked to activate a cell it
    /// had never heard of. That bought three rules about when the two sides were
    /// allowed to disagree, and the disagreement leaked anyway.
    /// </summary>
    private static TabInstance CaptureRuntimeTab(RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var panels = tab.Panels.ToArray();
        if (panels.Length == 0)
        {
            throw new InvalidOperationException("A runtime tab must have a panel.");
        }

        return new TabInstance(
            tab.Id,
            tab.Title,
            panels.Select(panel => new PanelInstance(
                panel.Id,
                panel.Kind,
                panel.Title)),
            tab.ActivePanelId ?? panels[0].Id);
    }

    private static WorkspaceInstance? BuildTabAppendProposal(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        return current.Tabs.Any(item => item.Id == tab.Id)
            ? null
            : new WorkspaceInstance(
                current.Id,
                current.Title,
                current.Tabs.Append(CaptureRuntimeTab(tab)),
                tab.Id);
    }

    private static WorkspaceInstance? BuildPanelRemovalProposal(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId tabId,
        PanelInstanceId panelId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        var currentTab = current.Tabs.SingleOrDefault(item => item.Id == tabId);
        if (currentTab is null
            || currentTab.Panels.Count <= 1
            || currentTab.Panels.All(item => item.Id != panelId))
        {
            return null;
        }

        var remainingPanels = currentTab.Panels
            .Where(item => item.Id != panelId)
            .ToArray();
        var removedIndex = currentTab.Panels
            .Select((item, index) => (item, index))
            .Single(item => item.item.Id == panelId)
            .index;
        var activePanelId = currentTab.ActivePanelId == panelId
            ? remainingPanels[Math.Min(removedIndex, remainingPanels.Length - 1)].Id
            : currentTab.ActivePanelId;
        return ReplaceRuntimeTab(
            current,
            new TabInstance(
                currentTab.Id,
                currentTab.Title,
                remainingPanels,
                activePanelId),
            current.ActiveTabId);
    }

    private static WorkspaceInstance? BuildTabRemovalProposal(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId tabId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        if (current.Tabs.Count <= 1
            || current.Tabs.All(item => item.Id != tabId))
        {
            return null;
        }

        var remainingTabs = current.Tabs.Where(item => item.Id != tabId).ToArray();
        // The tab before the one going, or the one that takes its place at the
        // front. This has to be the same choice the commit makes, or the client
        // activates one tab while the host was told another.
        var removedAt = 0;
        for (var index = 0; index < current.Tabs.Count; index++)
        {
            if (current.Tabs[index].Id == tabId)
            {
                removedAt = index;
                break;
            }
        }

        var activeTabId = current.ActiveTabId == tabId
            ? remainingTabs[Math.Max(0, removedAt - 1)].Id
            : current.ActiveTabId;
        return new WorkspaceInstance(
            current.Id,
            current.Title,
            remainingTabs,
            activeTabId);
    }

    private static WorkspaceInstance AppendRuntimePanel(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        PanelInstance panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(panel);
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        var replacement = new TabInstance(
            tab.Id,
            tab.Title,
            tab.Panels.Append(panel),
            panel.Id);
        return ReplaceRuntimeTab(workspace, replacement, tabId);
    }

    /// <summary>
    /// Swaps one panel for another in place, which is what answering a placed cell
    /// does: the cell was already part of the graph, so the tab does not grow.
    /// </summary>
    private static WorkspaceInstance ReplaceRuntimePanel(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        PanelInstanceId replacedPanelId,
        PanelInstance panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(panel);
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        var replacement = new TabInstance(
            tab.Id,
            tab.Title,
            tab.Panels.Select(item => item.Id == replacedPanelId ? panel : item),
            panel.Id);
        return ReplaceRuntimeTab(workspace, replacement, tabId);
    }

    private static WorkspaceInstance RenameRuntimeTab(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        string title)
    {
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        return ReplaceRuntimeTab(
            workspace,
            new TabInstance(tab.Id, title, tab.Panels, tab.ActivePanelId),
            workspace.ActiveTabId);
    }

    private static WorkspaceInstance ReplaceRuntimeTab(
        WorkspaceInstance workspace,
        TabInstance replacement,
        TabInstanceId activeTabId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacement);
        if (workspace.Tabs.All(tab => tab.Id != replacement.Id))
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacement),
                "The replacement tab must belong to the runtime workspace.");
        }

        return new WorkspaceInstance(
            workspace.Id,
            workspace.Title,
            workspace.Tabs.Select(tab => tab.Id == replacement.Id ? replacement : tab),
            activeTabId);
    }

    private void TrackRecentSession(RuntimePanelViewModel panel) =>
        TrackRecentSessions([panel]);

    private void TrackRecentSessions(IEnumerable<RuntimePanelViewModel> panels)
    {
        if (_recentSessionHistory is null)
        {
            return;
        }

        var pending = new List<RecentSessionRecord>();
        foreach (var panel in panels)
        {
            var source = ResolveRuntimeHistorySource(panel);
            var identity = panel switch
            {
                TerminalRuntimePanelViewModel { SessionRequest: { } request } =>
                    new RuntimeSessionIdentity(request.SessionId, PanelKind.Terminal),
                BrowserRuntimePanelViewModel { SessionRequest: { } request } =>
                    new RuntimeSessionIdentity(request.SessionId, PanelKind.Browser),
                FileRuntimePanelViewModel { HostedClient: { } hosted } =>
                    new RuntimeSessionIdentity(hosted.SessionId, PanelKind.FileViewer),
                StatisticsRuntimePanelViewModel { HasHostedSession: true } statistics =>
                    new RuntimeSessionIdentity(statistics.SessionId, PanelKind.Statistics),
                ProcessMonitorRuntimePanelViewModel { HasHostedSession: true } processes =>
                    new RuntimeSessionIdentity(processes.SessionId, PanelKind.ProcessMonitor),
                _ => null,
            };
            if (source is null
                || identity is null
                || !_recentSessionIds.TryAdd(panel.Id, identity.SessionId))
            {
                continue;
            }

            pending.Add(_recentSessionHistory.CaptureStarted(
                identity.SessionId,
                source.SourceDefinition,
                identity.Kind,
                source.DurableTitle));
        }

        if (pending.Count == 0)
        {
            return;
        }

        _ = QueueHistoryOperation(async token =>
        {
            foreach (var item in pending)
            {
                var result = await _recentSessionHistory.RecordStartedAsync(
                    item,
                    token);
                if (!result.IsSuccess)
                {
                    ApplyRecentSessionPersistenceFailure(result.Error!);
                    return;
                }
            }

            await RefreshRecentSessionsCoreAsync(token);
        });
    }

    private RuntimeHistorySource? ResolveRuntimeHistorySource(RuntimePanelViewModel panel)
    {
        var sourceTab = RuntimeWorkspace?.Tabs.FirstOrDefault(
            tab => tab.Panels.Contains(panel));
        return sourceTab?.HistorySource ?? _runtimeHistorySource;
    }

    private void RecordRecentSessionCompletions(HostResult<CloseScopeResult> result)
    {
        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            })
        {
            return;
        }

        var completions = completed.Sessions
            .Select(item => (item.SessionId, Outcome: MapRecentSessionOutcome(item.Outcome)))
            .Where(item => item.Outcome is not null)
            .Select(item => (item.SessionId, Outcome: item.Outcome!.Value))
            .ToArray();
        QueueRecentSessionCompletions(completions);
    }

    private void QueueRecentSessionCompletion(
        PanelInstanceId panelId,
        RecentSessionOutcome outcome)
    {
        if (!_recentSessionIds.Remove(panelId, out var sessionId))
        {
            return;
        }

        QueueRecentSessionCompletions([(sessionId, outcome)]);
    }

    private void QueueRemainingRecentSessionCompletions(RecentSessionOutcome outcome)
    {
        if (_recentSessionIds.Count == 0)
        {
            return;
        }

        var completions = _recentSessionIds.Values
            .Select(sessionId => (sessionId, outcome))
            .ToArray();
        _recentSessionIds.Clear();
        QueueRecentSessionCompletions(completions);
    }

    private void QueueRecentSessionCompletions(
        IReadOnlyList<(SessionId SessionId, RecentSessionOutcome Outcome)> completions)
    {
        if (_recentSessionHistory is null || completions.Count == 0)
        {
            return;
        }

        List<RecentSessionCompletion> captured = [];
        lock (_historyGate)
        {
            foreach (var item in completions)
            {
                // A session ends once. Two producers can reach for the same
                // panel at shutdown — its own close and the sweep that
                // follows — and the store rightly refuses to overwrite a
                // terminal outcome, so the duplicate is stopped here rather
                // than turned into an error nobody can act on.
                if (!_completedSessionIds.Add(item.SessionId))
                {
                    continue;
                }

                captured.Add(_recentSessionHistory.CaptureCompletion(
                    item.SessionId,
                    item.Outcome));
            }
        }

        if (captured.Count == 0)
        {
            return;
        }

        var capturedCompletions = captured.ToArray();
        var trackedSessionIds = capturedCompletions
            .Select(item => item.SessionId)
            .ToHashSet();
        foreach (var panelId in _recentSessionIds
            .Where(item => trackedSessionIds.Contains(item.Value))
            .Select(item => item.Key)
            .ToArray())
        {
            _recentSessionIds.Remove(panelId);
        }

        _ = QueueHistoryOperation(async token =>
        {
            foreach (var completion in capturedCompletions)
            {
                var result = await _recentSessionHistory.RecordCompletedAsync(
                    completion,
                    token);
                if (!result.IsSuccess)
                {
                    if (result.Error!.Code == RecentSessionStoreErrorCode.Conflict)
                    {
                        // The row already carries a terminal outcome, so this
                        // session's end is recorded — by an earlier
                        // notification, not this one. Nothing was lost, and
                        // the exit report must stay a report of loss to mean
                        // anything.
                        Console.Error.WriteLine(
                            "[ghostshell:history] A completion arrived for a session "
                            + "that had already ended; the recorded outcome stands.");
                        continue;
                    }

                    ApplyRecentSessionPersistenceFailure(result.Error!);
                    return;
                }
            }

            // Once the desktop lifetime has ended there is no visible history view to
            // refresh, and its dispatcher is no longer available. The durable completion
            // above is the only shutdown work this operation owns.
            if (!_shutdownStarted)
            {
                await RefreshRecentSessionsCoreAsync(token);
            }
        });
    }

    private Task QueueHistoryOperation(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_historyGate)
        {
            if (_historyOperationsSealed)
            {
                return _historyOperations;
            }

            _historyOperations = RunHistoryOperationAsync(
                _historyOperations,
                operation,
                _historyLifetime.Token);
            return _historyOperations;
        }
    }

    private async Task RunHistoryOperationAsync(
        Task previous,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = new RecentSessionStoreError(
                RecentSessionStoreErrorCode.StorageFailure,
                $"Recent-session metadata is temporarily unavailable ({exception.GetType().Name}).");
            Console.Error.WriteLine(
                $"[ghostshell:history] queued history operation failed: {exception}");
            ApplyRecentSessionPersistenceFailure(error);
        }
    }

    private async Task RefreshRecentSessionsCoreAsync(
        CancellationToken cancellationToken,
        bool replaceRetentionSelection = false)
    {
        if (_recentSessionHistory is null)
        {
            return;
        }

        if (_recentSessionHistory.SupportsRetentionSettings)
        {
            var retention = await _recentSessionHistory.GetRetentionAsync(cancellationToken);
            if (!retention.IsSuccess)
            {
                _storedHistoryRetention = null;
                _isApplyingStoredHistoryRetention = true;
                try
                {
                    SelectedHistoryRetentionOption = null;
                    HasPendingHistoryRetentionChange = false;
                }
                finally
                {
                    _isApplyingStoredHistoryRetention = false;
                }

                ApplyRecentSessionFailure(retention.Error!);
                HistoryRetentionStatus =
                    $"History privacy settings are unavailable ({retention.Error!.Code}).";
                OnPropertyChanged(nameof(CanManageHistoryRetention));
                return;
            }

            ApplyStoredHistoryRetention(
                retention.Value!,
                replaceRetentionSelection);
        }

        var result = await _recentSessionHistory.ListRecentAsync(
            RecentSessionQuery.MaximumLimit,
            cancellationToken);
        if (!result.IsSuccess)
        {
            ApplyRecentSessionFailure(result.Error!);
            return;
        }

        var observedAt = _timeProvider.GetUtcNow();
        var items = result.Value!
            .Select(record => ToRecentSessionItem(record, observedAt))
            .ToArray();
        HasRecentSessionFailure = false;
        HasUnreadableRecentSessionHistory = false;
        ReplaceIfChanged(HistorySessions, items, static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            RecentSessions,
            items.Take(8).ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        // The durable half was just replaced, so whatever the runtime half said
        // went with it.
        RefreshWorkspaceRuntimeFlags();
        RecentSessionStatus = HistorySessions.Count > 0
            ? "Recent sessions store definition metadata only; commands and terminal content are excluded."
            : _storedHistoryRetention is { Policy: { IsEnabled: false } }
                ? "Session history is disabled in the local privacy settings."
                : "Sessions you open will appear here without storing terminal content or commands.";
        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(HasNoRecentSessions));
        OnPropertyChanged(nameof(HasHistorySessions));
        OnPropertyChanged(nameof(HasNoHistorySessions));
        RefreshHistorySearchResults();
        RefreshLauncherSearchResults();
        NotifyHistoryActionStateChanged();
    }

    /// <summary>
    /// Pushes the saved terminal profile to every open panel.
    ///
    /// A panel captured its render profile when it launched, so changing the
    /// terminal font changed the stored definition and nothing visible: open
    /// panels kept the size they started with until they were closed and
    /// reopened. Typography is applied in place — restarting the session to change
    /// a font would throw away the scrollback with it.
    /// </summary>
    private void RefreshOpenTerminalRenderProfiles()
    {
        if (ActiveTerminalProfile is not { } profile || RuntimeWorkspace is null)
        {
            return;
        }

        var snapshot = TerminalRenderProfileSnapshot.FromProfile(profile);
        foreach (var panel in RuntimeWorkspace.Tabs
                     .SelectMany(tab => tab.Panels)
                     .OfType<TerminalRuntimePanelViewModel>())
        {
            panel.RenderProfile = snapshot;
        }
    }

    private RecentSessionHistoryItemViewModel ToRecentSessionItem(
        RecentSessionRecord record,
        DateTimeOffset observedAt)
    {
        // A row is read as "what would I reconnect to", so it carries the saved
        // definition's transport and endpoint rather than only the session's own
        // metadata. Resolving it here — instead of storing it in history — keeps
        // the row truthful after the connection is edited, and leaves it null for
        // a definition that no longer exists.
        var connection = record.SourceDefinition.Kind == ConnectionProfile.Kind
            ? Connections.FirstOrDefault(item => item.Id.Value == record.SourceDefinition.Value)
            : null;

        return new RecentSessionHistoryItemViewModel(
            record,
            CanOpenDefinition(record.SourceDefinition),
            observedAt,
            connection?.Kind,
            connection?.Detail);
    }

    private void ApplyStoredHistoryRetention(
        StoredRecentSessionRetentionPolicy stored,
        bool replaceSelection = false)
    {
        _storedHistoryRetention = stored;
        var option = HistoryRetentionOptions.FirstOrDefault(item => item.Policy == stored.Policy);
        if (option is null)
        {
            option = new HistoryRetentionOption(
                $"Custom · {stored.Policy.MaximumEntries:N0} / {stored.Policy.MaximumAge.TotalDays:0} days",
                $"Keep at most {stored.Policy.MaximumEntries:N0} records for up to {stored.Policy.MaximumAge.TotalDays:0} days.",
                stored.Policy);
            HistoryRetentionOptions.Add(option);
        }

        if (replaceSelection
            || !HasPendingHistoryRetentionChange
            || SelectedHistoryRetentionOption is null)
        {
            _isApplyingStoredHistoryRetention = true;
            try
            {
                SelectedHistoryRetentionOption = option;
                HasPendingHistoryRetentionChange = false;
            }
            finally
            {
                _isApplyingStoredHistoryRetention = false;
            }
        }
        else if (SelectedHistoryRetentionOption.Policy == stored.Policy)
        {
            HasPendingHistoryRetentionChange = false;
        }
        HistoryRetentionStatus = stored.Policy.IsEnabled
            ? $"Local metadata retention: up to {stored.Policy.MaximumEntries:N0} records for {stored.Policy.MaximumAge.TotalDays:0} days."
            : "Session history is disabled; newly opened sessions will not be retained.";
        OnPropertyChanged(nameof(CanManageHistoryRetention));
        OnPropertyChanged(nameof(RequiresHistoryRetentionConfirmation));
    }

    private void RefreshHistorySearchResults(bool preserveSelection = true)
    {
        var selectedSessionId = preserveSelection ? SelectedHistorySession?.SessionId : null;
        var results = RecentSessionHistoryProjection.Search(HistorySearchQuery, HistorySessions);
        ReplaceIfChanged(
            FilteredHistorySessions,
            results,
            static (a, b) => a.PresentsSameAs(b));
        SelectedHistorySession = RecentSessionHistoryProjection.ResolveSelection(
            results,
            selectedSessionId);
        OnPropertyChanged(nameof(HasFilteredHistorySessions));
        OnPropertyChanged(nameof(HasNoFilteredHistorySessions));
        OnPropertyChanged(nameof(HistoryResultCount));
        OnPropertyChanged(nameof(HistorySearchEmptyState));
        OnPropertyChanged(nameof(CanExportFilteredHistory));
    }

    private void ApplyRecentSessionFailure(RecentSessionStoreError error)
    {
        HasRecentSessionFailure = true;
        HasUnreadableRecentSessionHistory =
            error.Code == RecentSessionStoreErrorCode.InvalidHistoryData;
        HistorySessions.Clear();
        RecentSessions.Clear();
        RecentSessionStatus = $"Recent-session metadata is unavailable ({error.Code}).";
        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(HasNoRecentSessions));
        OnPropertyChanged(nameof(HasHistorySessions));
        OnPropertyChanged(nameof(HasNoHistorySessions));
        RefreshHistorySearchResults();
        RefreshLauncherSearchResults();
        NotifyHistoryActionStateChanged();
    }

    private void ApplyRecentSessionPersistenceFailure(
        RecentSessionStoreError error)
    {
        lock (_historyGate)
        {
            _historyDrainError ??= error;
        }

        ApplyRecentSessionFailure(error);
    }

    private void NotifyHistoryActionStateChanged()
    {
        OnPropertyChanged(nameof(HasNoRecentSessions));
        OnPropertyChanged(nameof(HasNoFilteredHistorySessions));
        OnPropertyChanged(nameof(CanRetryRecentSessionHistory));
        OnPropertyChanged(nameof(CanClearRecentSessionHistory));
        OnPropertyChanged(nameof(CanResetRecentSessionHistory));
        OnPropertyChanged(nameof(CanExportAllHistory));
        OnPropertyChanged(nameof(CanExportFilteredHistory));
        OnPropertyChanged(nameof(CanManageHistoryRetention));
        OnPropertyChanged(nameof(CanApplyHistoryRetention));
    }

    private bool CanOpenDefinition(DefinitionKey key) => key.Kind switch
    {
        var kind when kind == ConnectionProfile.Kind => Connections
            .Any(item => item.Id.Value == key.Value && item.CanOpen),
        var kind when kind == ScreenDefinition.Kind => _catalog.Snapshot.Screens
            .Any(item => item.Value.Key == key),
        var kind when kind == WorkspaceDefinition.Kind => _catalog.Snapshot.Workspaces
            .Any(item => item.Value.Key == key),
        _ => false,
    };

    private static RecentSessionOutcome? MapRecentSessionOutcome(
        SessionCloseOutcome outcome) => outcome switch
        {
            SessionCloseOutcome.GracefullyClosed or SessionCloseOutcome.AlreadyClosed =>
                RecentSessionOutcome.GracefullyClosed,
            SessionCloseOutcome.ForceTerminated => RecentSessionOutcome.ForceTerminated,
            SessionCloseOutcome.EngineFailed => RecentSessionOutcome.Failed,
            SessionCloseOutcome.Cancelled or SessionCloseOutcome.ConfirmationRequired => null,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    private void BeginEdit(
        DefinitionKey key,
        long revision,
        string name,
        string? description)
    {
        _editingDefinition = key;
        _editingRevision = revision;
        EditorName = name;
        EditorDescription = description ?? string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        ShowOverlay(ShellOverlay.DefinitionEditor);
    }

    private bool TryDismissOverlayForNavigation()
    {
        if (LayoutDesignerEditor?.RequestCancel()
            == LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the layout changes before leaving this view.");
            return false;
        }

        if (WorkspaceEditor?.RequestCancel()
            == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the workspace changes before leaving this view.");
            return false;
        }

        LayoutDesignerEditor = null;
        WorkspaceEditor = null;
        _editingDefinition = null;
        _editingRevision = null;
        Overlay = ShellOverlay.None;
        return true;
    }

    /// <summary>
    /// Rebuilds every launcher list from a catalog snapshot.
    ///
    /// Internal rather than private so a test can drive it directly: the catalog's
    /// own notification hops through the dispatcher, and in a unit test the UI
    /// thread is whichever thread happened to touch Avalonia first, so the hop may
    /// never be pumped.
    /// </summary>
    internal void RefreshCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ReplaceIfChanged(
            Workspaces,
            snapshot.Workspaces
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new LauncherWorkspaceViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    item.Value.Description ?? "No description",
                    // The tile carries the workspace's identity colour, which is
                    // its own field now; an accent-only workspace keeps looking
                    // the way it did before colours existed.
                    WorkspaceTints.Of(item.Value),
                    Initials(item.Value.Name),
                    WorkspaceIconSymbol(item.Value.Icon),
                    item.Value.Entries.Count))
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        // A rail item that was replaced arrives with its runtime flags cleared,
        // and the flags are derived rather than stored, so nothing else would
        // put them back. Saving the workspace you are working in bumps its
        // revision — which is exactly what autosave does every time a tab is
        // added — and the rail forgot which workspace you were in.
        RefreshWorkspaceRuntimeFlags();
        ReplaceIfChanged(
            Connections,
            snapshot.Connections
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToConnectionItem(item.Value, item.Revision))
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            FileConnections,
            snapshot.FileProviderProfiles
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToFileConnectionItem(item.Value, item.Revision))
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            DatabaseConnections,
            snapshot.DatabaseConnections
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToDatabaseConnectionItem(item.Value, item.Revision))
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        RefreshFileProviderDefinitions(snapshot);
        RefreshAiProviderDefinitions(snapshot);
        RefreshMcpServerDefinitions(snapshot);
        var layoutsById = snapshot.Layouts.ToDictionary(item => item.Value.Id, item => item.Value);
        ReplaceIfChanged(
            Screens,
            snapshot.Screens
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    layoutsById.TryGetValue(item.Value.LayoutId, out var layout);
                    return new LauncherScreenViewModel(
                        item.Value.Id,
                        item.Revision,
                        item.Value.Name,
                        item.Value.Description ?? "Reusable screen",
                        layout?.Name ?? "Missing layout",
                        item.Value.Panels.Count,
                        CreateScreenPreview(item.Value, layout),
                        ScreenSummary(item.Value, snapshot));
                })
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            Layouts,
            snapshot.Layouts
                .Where(item => !LayoutDefinition.IsAutoSaved(item.Value.Id))
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new LayoutCardViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    item.Value.Grid.Rows,
                    item.Value.Grid.Columns,
                    item.Value.Slots.Count,
                    CreateLayoutPreview(item.Value)))
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
        ReplaceIfChanged(
            ConnectionsPreview,
            Connections
                .Concat(FileConnections)
                .Concat(DatabaseConnections)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(HomePreviewConnectionCount)
                .ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            ScreensPreview,
            Screens.Take(HomePreviewScreenCount).ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(PanelConnectionOptions));
        OnPropertyChanged(nameof(BrowserConnectionOptions));
        OnPropertyChanged(nameof(DatabasePanelConnectionOptions));
        OnPropertyChanged(nameof(FileConnectionOptions));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(HasTerminalConnections));
        OnPropertyChanged(nameof(HasFileConnections));
        OnPropertyChanged(nameof(HasDatabaseConnections));
        OnPropertyChanged(nameof(TotalConnectionCount));
        OnPropertyChanged(nameof(HasScreens));
        OnPropertyChanged(nameof(HasNoScreens));
        OnPropertyChanged(nameof(HasMoreConnectionsThanPreview));
        OnPropertyChanged(nameof(HasMoreScreensThanPreview));
        var terminal = snapshot.TerminalProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (terminal is null)
        {
            TerminalSettingsEditor = null;
        }
        else if (TerminalSettingsEditor is null
            || TerminalSettingsEditor.ProfileId != terminal.Value.Id
            || TerminalSettingsEditor.ExpectedRevision != terminal.Revision
            || !TerminalSettingsEditor.MatchesTerminalKeymaps(
                snapshot.Keymaps.Select(item => item.Value)))
        {
            TerminalSettingsEditor = new TerminalProfileEditorViewModel(
                terminal.Value,
                terminal.Revision,
                snapshot.Keymaps.Select(item => item.Value));
        }

        var quickTerminal = snapshot.QuickTerminalSettings
            .OrderByDescending(item => item.Value.Id == QuickTerminalSettings.DefaultId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (quickTerminal is null)
        {
            QuickTerminalSettingsEditor = null;
        }
        else if (QuickTerminalSettingsEditor is null
            || QuickTerminalSettingsEditor.SettingsId != quickTerminal.Value.Id
            || QuickTerminalSettingsEditor.ExpectedRevision != quickTerminal.Revision)
        {
            QuickTerminalSettingsEditor = new QuickTerminalSettingsEditorViewModel(
                quickTerminal.Value,
                quickTerminal.Revision);
        }

        RefreshKeybindings(snapshot);
        RefreshOpenTerminalRenderProfiles();
        OnPropertyChanged(nameof(ActiveTheme));
        OnPropertyChanged(nameof(ActiveTerminalProfile));
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(ThemeProfile));
        OnPropertyChanged(nameof(ThemeTextScale));
        OnPropertyChanged(nameof(ThemeAccent));
        OnPropertyChanged(nameof(ShowTabBar));
        OnPropertyChanged(nameof(ShowWorkspacesPanel));
        // The mark on the menu stands in for the rail, so turning the rail on
        // or off decides whether it is needed.
        OnPropertyChanged(nameof(HasWorkspaceAttention));
        OnPropertyChanged(nameof(IsWorkspacePanelOnLeft));
        OnPropertyChanged(nameof(IsWorkspacePanelOnRight));
        OnPropertyChanged(nameof(WorkspacePanelDock));
        OnPropertyChanged(nameof(IsTabStripVisibleOnTop));
        OnPropertyChanged(nameof(IsTabStripVisibleOnBottom));
        OnPropertyChanged(nameof(IsTabStripVisibleOnSide));
        OnPropertyChanged(nameof(TabStripDock));
        OnPropertyChanged(nameof(IsTabStripDockedLeft));
        OnPropertyChanged(nameof(IsTabStripDockedRight));
        OnPropertyChanged(nameof(SideTabIconPickerPlacement));
        OnPropertyChanged(nameof(KeybindingConflictCount));
        RefreshRecentSessionAvailability();
        RefreshLauncherSearchResults();
    }

    private void RefreshRecentSessionAvailability()
    {
        for (var index = 0; index < HistorySessions.Count; index++)
        {
            var item = HistorySessions[index];
            var canOpen = CanOpenDefinition(item.SourceDefinition);
            if (item.CanOpen != canOpen)
            {
                HistorySessions[index] = item with { CanOpen = canOpen };
            }
        }

        ReplaceIfChanged(
            RecentSessions,
            HistorySessions.Take(8).ToArray(),
            static (a, b) => a.PresentsSameAs(b));
        RefreshHistorySearchResults();
    }

    /// <summary>
    /// The layout's own shape — a layout row without its geometry made every
    /// card read as "some grid". No slot is emphasized: unlike a screen, a
    /// layout has no primary panel, only regions.
    /// </summary>
    private static IReadOnlyList<LauncherScreenPanelPreviewViewModel> CreateLayoutPreview(
        LayoutDefinition layout) =>
        layout.Slots
            .Select(slot => new LauncherScreenPanelPreviewViewModel(
                layout.Grid.Columns,
                layout.Grid.Rows,
                slot.Bounds.Column,
                slot.Bounds.Row,
                slot.Bounds.ColumnSpan,
                slot.Bounds.RowSpan,
                IsPrimary: false))
            .ToArray();

    private static IReadOnlyList<LauncherScreenPanelPreviewViewModel> CreateScreenPreview(
        ScreenDefinition screen,
        LayoutDefinition? layout)
    {
        if (layout is null)
        {
            return [];
        }

        var slots = layout.Slots.ToDictionary(slot => slot.Id);
        return screen.Panels
            .Select((panel, index) => (panel, index))
            .Where(item => slots.ContainsKey(item.panel.SlotId))
            .Select(item =>
            {
                var bounds = slots[item.panel.SlotId].Bounds;
                return new LauncherScreenPanelPreviewViewModel(
                    layout.Grid.Columns,
                    layout.Grid.Rows,
                    bounds.Column,
                    bounds.Row,
                    bounds.ColumnSpan,
                    bounds.RowSpan,
                    item.index == 0);
            })
            .ToArray();
    }

    private void RefreshFileProviderDefinitions(DefinitionCatalogSnapshot snapshot)
    {
        var liveIds = _filePanelClient.Profiles
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var diagnostics = (_fileProviderRuntime?.Diagnostics ?? [])
            .Where(item => item.ProfileId is not null)
            .GroupBy(item => item.ProfileId!.Value)
            .ToDictionary(item => item.Key, item => item.ToArray());
        ReplaceIfChanged(
            FileProviderDefinitions,
            [.. snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                diagnostics.TryGetValue(item.Value.Id, out var profileDiagnostics);
                var error = profileDiagnostics?.FirstOrDefault(diagnostic =>
                    diagnostic.Severity == FileProviderRuntimeDiagnosticSeverity.Error);
                var warning = profileDiagnostics?.FirstOrDefault(diagnostic =>
                    diagnostic.Severity == FileProviderRuntimeDiagnosticSeverity.Warning);
                var isLive = liveIds.Contains(item.Value.Id.Value);
                return new FileProviderProfileItemViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    FileProviderKindLabel(item.Value.ProviderKind),
                    FileProviderEndpoint(item.Value.Configuration),
                    error is not null ? "Unavailable" : isLive ? "Ready" : "Loading",
                    error?.Message
                        ?? warning?.Message
                        ?? (isLive
                            ? "Adapter loaded; credentials resolve only when the provider is used."
                            : "Materializing the saved adapter…"),
                    error is not null,
                    warning is not null);
            })],
            static (a, b) => a == b);
        OnPropertyChanged(nameof(FileProviderProfiles));
        OnPropertyChanged(nameof(FileConnectionOptions));
        OnPropertyChanged(nameof(SavedConnectionShortcuts));
        OnPropertyChanged(nameof(SavedConnectionShortcutCount));
    }

    private IReadOnlyList<SavedConnectionShortcutViewModel> BuildSavedConnectionShortcuts()
    {
        var connectionItems = Connections.ToDictionary(item => item.Id);
        var shortcuts = _catalog.Snapshot.Connections
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                connectionItems.TryGetValue(item.Value.Id, out var launchItem);
                return CreateSavedConnectionShortcut(
                    new PanelConnectionOptionViewModel.Target.Connection(item.Value.Id),
                    item.Value.Name,
                    KindBadges.Connection(item.Value.ConnectionKind),
                    launchItem?.Detail ?? string.Empty,
                    launchItem is { CanOpen: true },
                    item.Value.Endpoint.PanelLaunchCapabilities);
            })
            .ToList();

        var liveFileProfiles = _filePanelClient.Profiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        shortcuts.AddRange(_catalog.Snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreateSavedConnectionShortcut(
                new PanelConnectionOptionViewModel.Target.FileProvider(item.Value.Id),
                item.Value.Name,
                FileProviderKindLabel(item.Value.ProviderKind),
                FileProviderEndpoint(item.Value.Configuration),
                liveFileProfiles.Contains(item.Value.Id.Value),
                item.Value.Configuration.PanelLaunchCapabilities)));
        return shortcuts
            .OrderBy(shortcut => shortcut.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(shortcut => shortcut.Kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SavedConnectionShortcutViewModel CreateSavedConnectionShortcut(
        PanelConnectionOptionViewModel.Target target,
        string name,
        string kind,
        string detail,
        bool canOpen,
        PanelLaunchCapabilities capabilities)
    {
        var launches = capabilities.SupportedPanels
            .Select(panel => new SavedConnectionLaunchViewModel(
                target,
                panel,
                PanelLaunchLabel(panel),
                PanelLaunchIcon(panel)))
            .ToArray();
        var defaultLaunch = launches.Single(launch =>
            launch.Panel == capabilities.DefaultPanel);
        return new SavedConnectionShortcutViewModel(
            target,
            name,
            kind,
            detail,
            canOpen,
            defaultLaunch,
            launches.Where(launch => launch != defaultLaunch).ToArray());
    }

    private static string PanelLaunchLabel(PanelKind panel) => panel switch
    {
        PanelKind.Terminal => "Open terminal",
        PanelKind.FileViewer => "Open files",
        PanelKind.Statistics => "Open statistics",
        PanelKind.ProcessMonitor => "Open processes",
        PanelKind.Docker => "Open Docker",
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
    };

    private static Symbol PanelLaunchIcon(PanelKind panel) => panel switch
    {
        PanelKind.Terminal => Symbol.WindowConsole,
        PanelKind.FileViewer => Symbol.Folder,
        PanelKind.Statistics => Symbol.PulseSquare,
        PanelKind.ProcessMonitor => Symbol.Gauge,
        PanelKind.Docker => Symbol.Box,
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
    };

    private IReadOnlyList<PanelConnectionOptionViewModel> BuildFileConnectionOptions()
    {
        var liveProfiles = _filePanelClient.Profiles.ToDictionary(
            profile => profile.Id,
            StringComparer.Ordinal);
        var options = _catalog.Snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.FileProvider(item.Value.Id),
                item.Value.Name,
                FileProviderKindLabel(item.Value.ProviderKind),
                FileProviderEndpoint(item.Value.Configuration),
                liveProfiles.ContainsKey(item.Value.Id.Value)))
            .ToList();
        var durableIds = _catalog.Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        options.AddRange(liveProfiles.Values
            .Where(profile => !durableIds.Contains(profile.Id))
            .Select(profile => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.FileProvider(
                    new FileProviderProfileId(profile.Id)),
                profile.Name,
                FileProviderFamilyLabel(profile.Family),
                FileProviderDetail(profile),
                true)));
        return options
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RefreshAiProviderDefinitions(DefinitionCatalogSnapshot snapshot)
    {
        var descriptors = (_aiProviderRuntime?.Profiles ?? [])
            .ToDictionary(item => item.Id);
        var diagnostics = (_aiProviderRuntime?.Diagnostics ?? [])
            .Where(item => item.ProfileId is not null)
            .GroupBy(item => item.ProfileId!.Value)
            .ToDictionary(item => item.Key, item => item.ToArray());
        ReplaceIfChanged(
            AiProviderDefinitions,
            [.. snapshot.AiProviderProfiles
            .OrderBy(item => item.Value.Order)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                descriptors.TryGetValue(item.Value.Id, out var descriptor);
                diagnostics.TryGetValue(item.Value.Id, out var profileDiagnostics);
                var error = profileDiagnostics?.FirstOrDefault(diagnostic =>
                    diagnostic.Severity == AiProviderRuntimeDiagnosticSeverity.Error);
                var warning = profileDiagnostics?.FirstOrDefault(diagnostic =>
                    diagnostic.Severity == AiProviderRuntimeDiagnosticSeverity.Warning);
                var needsCredential = item.Value.Authentication
                    is AiProviderAuthentication.ApiKey apiKey
                    && Secrets.All(secret => secret.Reference != apiKey.Secret);
                var status = !item.Value.IsEnabled
                    ? "Disabled"
                    : error is not null
                        ? "Unavailable"
                        : needsCredential
                            ? "Credential missing"
                            : descriptor is null
                                ? "Loading"
                                : "Ready";
                return new AiProviderProfileItemViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    item.Value.ProviderKind switch
                    {
                        AiProviderKind.OpenAi => "OpenAI",
                        AiProviderKind.OpenAiCompatible => "OpenAI compatible",
                        AiProviderKind.Anthropic => "Anthropic",
                        _ => item.Value.ProviderKind.ToString(),
                    },
                    item.Value.Endpoint.AbsoluteUri,
                    item.Value.DefaultModel,
                    item.Value.Order,
                    status,
                    error?.Message
                        ?? warning?.Message
                        ?? (needsCredential
                            ? "Store the profile-scoped API key in the OS vault before testing."
                            : item.Value.IsEnabled
                                ? "Configuration loaded; credentials resolve only for a bounded request."
                                : "This provider is excluded from fallback selection."),
                    item.Value.IsEnabled,
                    error is not null || needsCredential,
                    warning is not null,
                    needsCredential);
            })],
            static (a, b) => a == b);
        OnPropertyChanged(nameof(AiProviderProfiles));
        OnPropertyChanged(nameof(HasAiProviders));
        OnPropertyChanged(nameof(HasNoAiProviders));
    }

    private void RefreshMcpServerDefinitions(DefinitionCatalogSnapshot snapshot)
    {
        PruneMcpServerTests(snapshot);
        var projectedProfiles = snapshot.McpServerProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ProjectMcpServerProfile(
                item,
                Secrets,
                GetMcpServerTest(item),
                _mcpServerDiagnostics is not null))
            .ToArray();
        ReconcileMcpServerDefinitions(projectedProfiles);
        Replace(McpEnvironmentSecretTargets, snapshot.McpServerProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(item => item.Value.Environment
                .Where(binding => Secrets.All(secret =>
                    secret.Reference != binding.Reference
                    || secret.SecretScope.Kind != SecretScopeKind.McpServer
                    || !string.Equals(
                        secret.SecretScope.OwnerId,
                        item.Value.Id.Value,
                        StringComparison.Ordinal)))
                .Select(binding => new McpEnvironmentSecretTargetViewModel(
                    item.Value.Id,
                    item.Value.Name,
                    binding.Name,
                    binding.Reference))));
        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(HasNoMcpServers));
        OnPropertyChanged(nameof(HasMcpEnvironmentSecretTargets));
    }

    private void ReconcileMcpServerDefinitions(
        IReadOnlyList<McpServerProfileItemViewModel> projectedProfiles)
    {
        for (var targetIndex = 0;
             targetIndex < projectedProfiles.Count;
             targetIndex++)
        {
            var projected = projectedProfiles[targetIndex];
            var existingIndex = FindMcpServerDefinitionIndex(
                projected.Id,
                targetIndex);
            if (existingIndex < 0)
            {
                McpServerDefinitions.Insert(targetIndex, projected);
                continue;
            }

            var existing = McpServerDefinitions[existingIndex];
            existing.UpdateFrom(projected);
            if (existingIndex != targetIndex)
            {
                McpServerDefinitions.Move(existingIndex, targetIndex);
            }
        }

        while (McpServerDefinitions.Count > projectedProfiles.Count)
        {
            McpServerDefinitions.RemoveAt(McpServerDefinitions.Count - 1);
        }
    }

    private int FindMcpServerDefinitionIndex(
        McpServerProfileId profileId,
        int startIndex)
    {
        for (var index = startIndex;
             index < McpServerDefinitions.Count;
             index++)
        {
            if (McpServerDefinitions[index].Id == profileId)
            {
                return index;
            }
        }

        return -1;
    }

    internal static McpServerProfileItemViewModel ProjectMcpServerProfile(
        StoredDefinition<McpServerProfile> stored,
        IReadOnlyCollection<SecretMetadataViewModel> secrets) =>
        ProjectMcpServerProfile(
            stored,
            secrets,
            test: null,
            diagnosticsAvailable: false);

    private static McpServerProfileItemViewModel ProjectMcpServerProfile(
        StoredDefinition<McpServerProfile> stored,
        IReadOnlyCollection<SecretMetadataViewModel> secrets,
        McpServerTestPresentation? test,
        bool diagnosticsAvailable)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(secrets);
        var profile = stored.Value;
        var missingSecretCount = profile.Environment.Count(binding =>
            secrets.All(secret =>
                secret.Reference != binding.Reference
                || secret.SecretScope.Kind != SecretScopeKind.McpServer
                || !string.Equals(
                    secret.SecretScope.OwnerId,
                    profile.Id.Value,
                    StringComparison.Ordinal)));
        var hasNoEnabledTools = profile.EnabledTools.Count == 0;
        var baselineStatus = !profile.IsEnabled
            ? "Disabled for future runs"
            : hasNoEnabledTools
                ? "No tools enabled"
                : missingSecretCount > 0
                    ? "Credential missing"
                    : "Enabled for new runs";
        var baselineDetail = !profile.IsEnabled
            ? "This saved configuration is excluded from new governed runs. Settings does not show live MCP process state."
            : hasNoEnabledTools
                ? "This saved configuration is excluded from new governed runs until its exact tool allowlist contains at least one name. Live process state is not shown here."
                : missingSecretCount > 0
                    ? missingSecretCount == 1
                        ? "One environment binding has no matching profile-scoped vault entry. Live process state is not shown here."
                        : $"{missingSecretCount} environment bindings have no matching profile-scoped vault entries. Live process state is not shown here."
                    : "This saved configuration is eligible for new governed runs. Settings does not show live MCP process state.";
        var currentTest = missingSecretCount == 0
            && test?.Revision == stored.Revision
                ? test
                : null;
        var isTesting = currentTest?.State
            == McpServerTestPresentationState.Testing;
        var testFailed = currentTest?.State
            == McpServerTestPresentationState.Failed;
        return new McpServerProfileItemViewModel(
            profile.Id,
            stored.Revision,
            profile.Name,
            profile.Executable,
            profile.Arguments.Count,
            profile.Environment.Count,
            profile.EnabledTools.Count,
            currentTest?.Status ?? baselineStatus,
            currentTest?.Detail ?? baselineDetail,
            profile.IsEnabled,
            (profile.IsEnabled
                && (hasNoEnabledTools || missingSecretCount > 0))
            || testFailed,
            isTesting,
            diagnosticsAvailable
                && profile.IsEnabled
                && missingSecretCount == 0
                && !isTesting);
    }

    private McpServerTestPresentation? GetMcpServerTest(
        StoredDefinition<McpServerProfile> stored)
    {
        lock (_mcpServerTestGate)
        {
            return _mcpServerTests.TryGetValue(
                    stored.Value.Id,
                    out var test)
                && test.Revision == stored.Revision
                    ? test
                    : null;
        }
    }

    private void SetMcpServerTest(
        McpServerProfileId profileId,
        McpServerTestPresentation presentation)
    {
        lock (_mcpServerTestGate)
        {
            _mcpServerTests[profileId] = presentation;
        }
    }

    private bool TryBeginMcpServerTest(
        McpServerProfileId profileId,
        long revision)
    {
        lock (_mcpServerTestGate)
        {
            if (_mcpServerTests.TryGetValue(
                    profileId,
                    out var current)
                && current.Revision == revision
                && current.State
                    == McpServerTestPresentationState.Testing)
            {
                return false;
            }

            _mcpServerTests[profileId] =
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Testing,
                    "Testing",
                    "Starting a bounded test session for initialization and tool discovery only…");
            return true;
        }
    }

    private void CompleteMcpServerTest(
        McpServerProfileId profileId,
        long revision,
        McpServerTestPresentation presentation)
    {
        lock (_mcpServerTestGate)
        {
            if (_mcpServerTests.TryGetValue(
                    profileId,
                    out var current)
                && current.Revision == revision
                && current.State
                    == McpServerTestPresentationState.Testing)
            {
                _mcpServerTests[profileId] = presentation;
            }
        }
    }

    private void PruneMcpServerTests(
        DefinitionCatalogSnapshot snapshot)
    {
        var revisions = snapshot.McpServerProfiles.ToDictionary(
            stored => stored.Value.Id,
            stored => stored.Revision);
        lock (_mcpServerTestGate)
        {
            foreach (var profileId in _mcpServerTests.Keys.ToArray())
            {
                if (!revisions.TryGetValue(
                        profileId,
                        out var revision)
                    || _mcpServerTests[profileId].Revision != revision)
                {
                    _mcpServerTests.Remove(profileId);
                }
            }
        }
    }

    private void InvalidateMcpServerTests(SecretRef reference)
    {
        var snapshot = _catalog.Snapshot;
        var affectedProfileIds = snapshot.McpServerProfiles
            .Where(stored => UsesSecret(stored.Value, reference))
            .Select(stored => stored.Value.Id)
            .ToHashSet();
        if (affectedProfileIds.Count == 0)
        {
            return;
        }

        lock (_mcpServerTestGate)
        {
            foreach (var profileId in affectedProfileIds)
            {
                _mcpServerTests.Remove(profileId);
            }
        }

        RefreshMcpServerDefinitions(snapshot);
    }

    private static McpServerTestPresentation
        CreateMcpServerTestSuccess(
            StoredDefinition<McpServerProfile> stored,
            McpServerTestReport report)
    {
        var profile = stored.Value;
        if (report.ProfileId != profile.Id
            || report.Revision != stored.Revision
            || report.EnabledToolCount != profile.EnabledTools.Count)
        {
            return new McpServerTestPresentation(
                stored.Revision,
                McpServerTestPresentationState.Failed,
                "Test failed",
                "The MCP diagnostics result did not match the saved profile revision and allowlist.");
        }

        var discovered = report.DiscoveredToolCount == 1
            ? "1 tool"
            : $"{report.DiscoveredToolCount} tools";
        var enabled = report.EnabledToolCount == 1
            ? "1 saved allowlist entry matched."
            : $"{report.EnabledToolCount} saved allowlist entries matched.";
        var eligibility = report.EnabledToolCount == 0
                ? " No tools are enabled for agent runs."
                : string.Empty;
        var completed = report.CompletedAtUtc.ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            System.Globalization.CultureInfo.InvariantCulture);
        return new McpServerTestPresentation(
            stored.Revision,
            McpServerTestPresentationState.Succeeded,
            "Last test passed",
            $"Tested {completed}. Bounded initialization discovered {discovered}; {enabled} "
                + "The test session closed its directly launched process without calling a tool. "
                + "Settings does not show live process state for governed runs."
                + eligibility
                + " Server-supplied identifiers are withheld from diagnostics.");
    }

    private void RefreshKeybindings(DefinitionCatalogSnapshot snapshot)
    {
        var transient = SelectedKeybindingProfile is { IsUnsaved: true } unsaved
            ? unsaved
            : null;
        var profiles = snapshot.Keymaps
            .OrderBy(item => item.Value.Layer)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new KeybindingProfileItemViewModel(
                item.Value.Id,
                item.Revision,
                item.Value.Name,
                item.Value.Layer,
                IsBuiltInKeymap(item.Value.Id),
                IsUnsaved: false))
            .ToList();
        if (transient is not null && profiles.All(item => item.Id != transient.Id))
        {
            profiles.Add(transient);
        }

        Replace(KeybindingProfiles, profiles);
        if (SelectedKeybindingProfile is { } selected)
        {
            SelectedKeybindingProfile = KeybindingProfiles
                .SingleOrDefault(item => item.Id == selected.Id);
        }

        var rows = new List<KeybindingRowViewModel>();
        foreach (var profile in snapshot.Keymaps.Select(item => item.Value))
        {
            var issues = KeymapConflictValidator.Validate(profile, BuiltInCommands.Registry);
            for (var bindingIndex = 0; bindingIndex < profile.Bindings.Count; bindingIndex++)
            {
                var binding = profile.Bindings[bindingIndex];
                _ = BuiltInCommands.Registry.TryGet(binding.CommandId, out var command);
                var hasConflict = issues.Any(issue =>
                    issue.BindingIndex == bindingIndex
                    || issue.OtherBindingIndex == bindingIndex);
                rows.Add(new(
                    command?.Category ?? "Unknown",
                    command?.Title ?? binding.CommandId.Value,
                    KeySequenceDisplay.Format(binding.Sequence),
                    profile.Name,
                    hasConflict ? "Conflict" : "Active",
                    hasConflict));
            }
        }

        ReplaceIfChanged(
            Keybindings,
            [.. rows
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Command, StringComparer.OrdinalIgnoreCase)],
            static (a, b) => a == b);
        OnPropertyChanged(nameof(KeybindingConflictCount));
        OnPropertyChanged(nameof(ActiveApplicationKeymap));
        OnPropertyChanged(nameof(ActiveApplicationKeymapRevision));
        OnPropertyChanged(nameof(ActiveApplicationKeymapName));
        RefreshLauncherSearchResults();
    }

    private static StoredDefinition<KeymapProfile> ResolveActiveApplicationKeymap(
        DefinitionCatalogSnapshot snapshot)
    {
        var custom = snapshot.Keymaps
            .Where(item => item.Value.Layer == KeymapLayer.Application)
            .Where(item => !IsBuiltInKeymap(item.Value.Id))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Revision)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (custom is not null)
        {
            return custom;
        }

        return snapshot.Keymaps
            .FirstOrDefault(item => item.Value.Id == BuiltInKeymaps.TmuxApplicationId)
            ?? new StoredDefinition<KeymapProfile>(
                BuiltInKeymaps.TmuxApplication,
                0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private void EnsureKeybindingEditor()
    {
        if (KeybindingEditorSession is not null)
        {
            return;
        }

        var selected = KeybindingProfiles
            .FirstOrDefault(item => item.Id == ActiveApplicationKeymap.Id)
            ?? KeybindingProfiles.FirstOrDefault(item => item.Id == BuiltInKeymaps.TmuxApplicationId)
            ?? KeybindingProfiles.FirstOrDefault();
        if (selected is not null)
        {
            SelectKeybindingProfile(selected);
        }
    }

    private void OpenKeybindingEditor(
        KeymapProfile profile,
        long revision,
        bool isReadOnly)
    {
        var resetSource = ResolveKeybindingResetSource(profile);
        var editor = KeybindingSettingsEditor.Edit(
            profile,
            revision,
            BuiltInCommands.Registry,
            resetSource);
        KeybindingEditorSession = new KeybindingEditorSessionViewModel(editor, isReadOnly);
    }

    private KeymapProfile ResolveKeybindingResetSource(KeymapProfile profile)
    {
        var resetId = profile.BasedOn ?? profile.Id;
        return BuiltInKeymaps.All.FirstOrDefault(item => item.Id == resetId)
            ?? _catalog.Snapshot.Keymaps
                .Select(item => item.Value)
                .FirstOrDefault(item => item.Id == resetId)
            ?? profile;
    }

    private static bool IsBuiltInKeymap(KeymapProfileId id) =>
        BuiltInKeymaps.All.Any(item => item.Id == id);

    private void RefreshLauncherSearchResults(bool preserveSelection = true)
    {
        var selectedTarget = preserveSelection ? SelectedLauncherSearchResult?.Target : null;
        var activeBindings = ActiveApplicationKeymap.Bindings
            .ToLookup(binding => binding.CommandId);
        var candidates = new List<LauncherSearchResultViewModel>();
        var savedDefinitionAction = HasRuntimeWorkspace ? "Add tab" : "Open";

        var canStartFileViewer = RuntimeWorkspace?.ActiveTab is not null
            || Workspaces.Count > 0
            || Connections.Any(connection => connection.CanOpen);
        var canStartBrowser = CanStartBrowserSession;
        var canStartDatabase = _databasePanelClient is not null;
        candidates.AddRange(
        [
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Terminal),
                Symbol.WindowConsole,
                "Create · terminal",
                "New terminal",
                "Start a local PTY in a new tab.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "terminal", "local", "pty", "panel", "tab"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Browser),
                Symbol.Globe,
                "Create · browser",
                "New browser",
                "Open an embedded Chromium browser panel.",
                canStartBrowser ? "Open" : "Unavailable",
                canStartBrowser,
                canStartBrowser
                    ? null
                    : "The embedded browser is unavailable in this build.",
                ["create", "new", "browser", "web", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.FileViewer),
                Symbol.Folder,
                "Create · files",
                "New File Viewer",
                "Browse local or configured file providers.",
                canStartFileViewer ? "Open" : "Unavailable",
                canStartFileViewer,
                canStartFileViewer
                    ? null
                    : "Open or save a workspace or connection first.",
                ["create", "new", "file", "files", "viewer", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Statistics),
                Symbol.PulseSquare,
                "Create · statistics",
                "New statistics panel",
                "Watch live system metrics on this host.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "statistics", "stats", "local", "host", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.ProcessMonitor),
                Symbol.Gauge,
                "Create · process monitor",
                "New process monitor",
                "Watch running processes on this host.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "process", "monitor", "local", "host", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.DatabaseViewer),
                Symbol.Database,
                "Create · database",
                "New database viewer",
                "Query SQLite, PostgreSQL, or MySQL.",
                canStartDatabase ? "Open" : "Unavailable",
                canStartDatabase,
                canStartDatabase
                    ? null
                    : "The database drivers are unavailable in this build.",
                ["create", "new", "database", "sql", "sqlite", "postgres", "mysql", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Docker),
                Symbol.Box,
                "Create · Docker",
                "New Docker panel",
                "Manage containers, images, volumes, and networks locally or over SSH.",
                _dockerEngineClient is null ? "Unavailable" : "Open",
                _dockerEngineClient is not null,
                _dockerEngineClient is null
                    ? "Docker support is unavailable in this build."
                    : null,
                ["create", "new", "docker", "container", "image", "volume", "network", "panel"]),
        ]);

        foreach (var command in BuiltInCommands.Registry.Commands)
        {
            var bindings = activeBindings[command.Id].ToArray();
            if (bindings.Length == 0)
            {
                bindings = command.DefaultBindings.ToArray();
            }

            var invocations = bindings
                .Select(binding => new
                {
                    Binding = binding,
                    Target = new LauncherSearchTarget.Command(
                        command.Id,
                        binding.Arguments),
                })
                .DistinctBy(invocation => invocation.Target.InvocationKey)
                .ToArray();
            if (invocations.Length == 0)
            {
                candidates.Add(CreateCommandLauncherResult(
                    command,
                    ImmutableDictionary<string, string>.Empty,
                    "Unbound"));
                continue;
            }

            candidates.AddRange(invocations.Select(invocation =>
                CreateCommandLauncherResult(
                    command,
                    invocation.Target.Arguments,
                    invocation.Binding.Sequence.ToString())));
        }

        candidates.AddRange(Connections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Connection(connection.Id),
            Symbol.Server,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                connection.Id.Value,
                connection.Kind,
                connection.Detail,
                connection.Status,
            ])));
        candidates.AddRange(FileConnections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.FileConnection(
                new FileProviderProfileId(connection.TargetId)),
            Symbol.Folder,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                "files",
                connection.TargetId,
                connection.Kind,
                connection.Detail,
            ])));
        candidates.AddRange(DatabaseConnections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.DatabaseConnection(
                new DatabaseConnectionProfileId(connection.TargetId)),
            Symbol.Database,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                "database",
                connection.TargetId,
                connection.Kind,
                connection.Detail,
            ])));
        candidates.AddRange(Screens.Select(screen => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Screen(screen.Id),
            Symbol.Grid,
            $"SAVED SCREEN · {screen.Layout}",
            screen.Name,
            $"{screen.Description} · {CountLabel(screen.PanelCount, "panel")}",
            savedDefinitionAction,
            IsAvailable: true,
            UnavailableReason: null,
            ["screen", "saved screen", screen.Id.Value, screen.Description, screen.Layout])));
        candidates.AddRange(Workspaces.Select(workspace => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Workspace(workspace.Id),
            workspace.IconSymbol,
            "Workspace",
            workspace.Name,
            workspace.Description,
            CountLabel(workspace.ItemCount, "item"),
            IsAvailable: true,
            UnavailableReason: null,
            ["workspace", workspace.Id.Value, workspace.Description])));
        candidates.AddRange(HistorySessions.Select(recent => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.RecentSession(recent.SessionId),
            Symbol.History,
            $"RECENT SESSION · {recent.SourceKind}",
            recent.Title,
            recent.Detail,
            recent.LastUsed,
            recent.CanOpen,
            recent.CanOpen
                ? null
                : "The current saved definition no longer exists or is unavailable on this platform.",
            [
                "recent",
                "recent session",
                recent.SessionId.Value,
                recent.SourceKind,
                recent.Detail,
            ])));

        var results = LauncherSearchProjection.Search(LauncherSearchQuery, candidates);

        // Rebuilding the list tears down every row, which moves whatever the
        // pointer is over while the pointer has not moved. Most refreshes are
        // triggered by something unrelated to the palette and produce exactly the
        // results already on screen, so those must touch nothing.
        if (!PresentsSameResults(LauncherSearchResults, results))
        {
            Replace(LauncherSearchResults, results);
            SelectedLauncherSearchResult = LauncherSearchProjection.ResolveAvailableSelection(
                results,
                selectedTarget);
        }

        OnPropertyChanged(nameof(HasLauncherSearchResults));
        OnPropertyChanged(nameof(HasNoLauncherSearchResults));
        OnPropertyChanged(nameof(LauncherSearchEmptyState));
    }

    private LauncherSearchResultViewModel CreateCommandLauncherResult(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> arguments,
        string shortcut)
    {
        var argumentSummary = string.Join(
            " · ",
            arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => argument.Value));
        var argumentDetail = string.Join(
            ", ",
            arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => $"{argument.Key}={argument.Value}"));
        var isAvailable = IsCommandAvailable(command.Id, arguments);
        var searchTerms = new List<string>
        {
            "command",
            command.Id.Value,
            command.Category,
        };
        foreach (var argument in arguments)
        {
            searchTerms.Add(argument.Key);
            searchTerms.Add(argument.Value);
            searchTerms.Add($"{argument.Key}={argument.Value}");
        }

        return new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Command(command.Id, arguments),
            Symbol.Code,
            $"COMMAND · {command.Category}",
            argumentSummary.Length == 0
                ? command.Title
                : $"{command.Title} · {argumentSummary}",
            argumentDetail.Length == 0
                ? command.Id.Value
                : $"{command.Id.Value} · {argumentDetail}",
            shortcut,
            isAvailable,
            isAvailable ? null : "Unavailable in the current route.",
            searchTerms);
    }

    private static string CountLabel(int count, string singular) =>
        $"{count} {(count == 1 ? singular : $"{singular}s")}";

    private bool IsCommandAvailable(
        CommandId id,
        IReadOnlyDictionary<string, string> arguments)
    {
        if (id == BuiltInCommands.NewTab)
        {
            return true;
        }

        if (!BuiltInCommands.Registry.TryGet(id, out var definition)
            || definition is null)
        {
            return false;
        }

        if (!definition.IsAvailable(new CommandInvocation(
                ActiveCommandContexts,
                arguments)))
        {
            return false;
        }

        if (id == BuiltInCommands.MoveTabLeft
            || id == BuiltInCommands.MoveTabRight)
        {
            var workspace = RuntimeWorkspace;
            var activeTab = workspace?.ActiveTab;
            if (workspace is null || activeTab is null || workspace.Tabs.Count < 2)
            {
                return false;
            }

            var activeIndex = workspace.Tabs.IndexOf(activeTab);
            return id == BuiltInCommands.MoveTabLeft
                ? activeIndex > 0
                : activeIndex < workspace.Tabs.Count - 1;
        }

        var hasTab = RuntimeWorkspace?.ActiveTab is not null;
        var hasPanel = ActivePanel is not null;
        var hasTerminal = ActivePanel is TerminalRuntimePanelViewModel;
        return (id == BuiltInCommands.SplitPanel && hasPanel)
            || (id == BuiltInCommands.FocusPanel && hasPanel)
            || (id == BuiltInCommands.TogglePanelZoom && hasPanel)
            || (id == BuiltInCommands.ClosePanel && hasPanel)
            || (id == BuiltInCommands.RenameTab && hasTab)
            || id == BuiltInCommands.CloseTab
            || id == BuiltInCommands.NextTab
            || id == BuiltInCommands.PreviousTab
            || id == BuiltInCommands.LastTab
            || id == BuiltInCommands.SelectTab
            || (id == BuiltInCommands.EnterTerminalCopyMode && hasTerminal)
            || (id == BuiltInCommands.SendPrefix && hasTerminal);
    }

    private RuntimeTabViewModel CreateConnectionTab(
        WorkspaceInstanceId workspaceId,
        ConnectionProfile connection,
        string? title = null,
        RuntimeAgentPolicyProvenance? agentPolicy = null)
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            string.IsNullOrWhiteSpace(title) ? connection.Name : title.Trim(),
            "Connection",
            historySource: new RuntimeHistorySource(connection.Key, connection.Name),
            agentPolicy: agentPolicy);
        try
        {
            AddPanelOrDispose(
                tab,
                CreatePanel(
                    workspaceId,
                    tab.Id,
                    new ScreenPanelDefinition(
                        ScreenPanelId.New(),
                        LayoutSlotId.New(),
                        ScreenPanelKind.Terminal,
                        connection.Name,
                        connection.Id,
                        PanelStartupBehavior.None)));
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimeTabViewModel CreateConnectionPanelTab(
        WorkspaceInstanceId workspaceId,
        ConnectionProfile connection,
        PanelKind panel,
        RuntimeAgentPolicyProvenance agentPolicy)
    {
        if (panel == PanelKind.Terminal)
        {
            return CreateConnectionTab(
                workspaceId,
                connection,
                agentPolicy: agentPolicy);
        }

        var title = PanelTitle(panel);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            title,
            connection.Name,
            historySource: new RuntimeHistorySource(connection.Key, connection.Name),
            agentPolicy: agentPolicy);
        try
        {
            var runtimePanel = panel switch
            {
                PanelKind.FileViewer => CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    connection.Endpoint is ConnectionEndpoint.Ssh
                        ? ConnectionFileProviderProfiles.Id(connection.Id)
                        : BuiltInFileProviders.HomeId,
                    deferInitialization: true,
                    connection: connection),
                PanelKind.Statistics or PanelKind.ProcessMonitor =>
                    CreateMonitorPanel(
                        workspaceId,
                        tab.Id,
                        PanelInstanceId.New(),
                        title,
                        panel,
                        connection),
                PanelKind.Docker => CreateDockerPanel(
                    PanelInstanceId.New(),
                    title,
                    connection),
                _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
            };
            AddPanelOrDispose(tab, runtimePanel);
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimeTabViewModel CreateFileProviderTab(
        WorkspaceInstanceId workspaceId,
        FileProviderProfile profile)
    {
        var title = PanelTitle(PanelKind.FileViewer);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            profile.Name,
            FileProviderKindLabel(profile.ProviderKind),
            historySource: new RuntimeHistorySource(profile.Key, profile.Name));
        try
        {
            AddPanelOrDispose(
                tab,
                CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    profile.Id,
                    deferInitialization: true));
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    /// <summary>
    /// The shell accent a restored workspace should come up wearing.
    ///
    /// The snapshot does not carry it — it records the colour the workspace is
    /// recognised by, which is a different field — so it is read back from the
    /// definition the restored workspace came from. A workspace with no
    /// definition behind it, or one whose definition is gone, leaves the shell
    /// wearing its own accent, which is what it would have done anyway.
    /// </summary>
    private string? ShellAccentOf(RuntimeWorkspaceViewModel? runtime)
    {
        if (runtime is null
            || !_runtimeSources.TryGetValue(runtime.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        return _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Id.Value == source.SourceDefinition.Value)
            ?.Value.Accent;
    }

    private RuntimeWorkspaceViewModel RestoreWorkspace(
        RuntimeWorkspaceRecoveryPayload recovered)
    {
        var connectionIds = recovered.ConnectionIds
            .Concat(recovered.Tabs
                .SelectMany(tab => tab.Panels)
                .Select(panel => panel.ConnectionId)
                .OfType<string>())
            .Select(id => new ConnectionId(id))
            .ToHashSet();
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            recovered.Name,
            recovered.Accent,
            Connections.Where(item => connectionIds.Contains(item.Id)).ToArray(),
            recovered.AgentPolicy?.ToProvenance()
                ?? RuntimeAgentPolicyProvenance.LegacyFallback,
            ResolveRecoveredTerminalMultiplexingOverride(recovered));
        if (runtime.TerminalMultiplexingMode is { } recoveredMultiplexingOverride)
        {
            _workspaceTerminalMultiplexingModes[runtime.Id] = recoveredMultiplexingOverride;
        }
        try
        {
            var restoredTabs = new Dictionary<string, RuntimeTabViewModel>(StringComparer.Ordinal);
            foreach (var recoveredTab in recovered.Tabs)
            {
                var tab = RestoreTab(runtime.Id, recoveredTab);
                runtime.Tabs.Add(tab);
                restoredTabs.Add(recoveredTab.Key, tab);
            }

            runtime.ActiveTab = recovered.ActiveTabKey is { } activeTabKey
                ? restoredTabs[activeTabKey]
                : runtime.Tabs[0];
            return runtime;
        }
        catch
        {
            runtime.DisposePanels();
            throw;
        }
    }

    private TerminalMultiplexingMode? ResolveRecoveredTerminalMultiplexingOverride(
        RuntimeWorkspaceRecoveryPayload recovered)
    {
        if (recovered.HistorySource is not
            { SourceKind: var kind, SourceValue: var value }
            || kind != WorkspaceDefinition.Kind.Value)
        {
            return recovered.TerminalMultiplexingMode;
        }

        return _catalog.Snapshot.Workspaces
            .Select(item => item.Value)
            .FirstOrDefault(workspace => workspace.Id.Value == value)
            ?.TerminalMultiplexingOverride;
    }

    private RuntimeTabViewModel RestoreTab(
        WorkspaceInstanceId workspaceId,
        RuntimeTabRecoveryPayload recovered)
    {
        var slots = recovered.Panels.Select(panel => new LayoutSlotDefinition(
            LayoutSlotId.New(),
            new LayoutGridBounds(
                panel.Column,
                panel.Row,
                panel.ColumnSpan,
                panel.RowSpan),
            new LayoutMinimumSize(panel.MinimumWidth, panel.MinimumHeight))).ToArray();
        var layout = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            "Recovered runtime layout",
            new LayoutGrid(recovered.Columns, recovered.Rows),
            slots,
            recovered.DockLayoutJson);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            recovered.Title,
            recovered.Source,
            // A recovery snapshot contains the authoritative Dock tree even
            // when the tab began in automatic-layout mode. Reconstructing a
            // fresh automatic layout here discards user docking, splitter and
            // floating-window changes captured later in the session.
            layout,
            recovered.HistorySource?.ToHistorySource(),
            recovered.AgentPolicy?.ToProvenance()
                ?? RuntimeAgentPolicyProvenance.LegacyFallback,
            usesAutomaticLayout: recovered.UsesAutomaticLayout,
            icon: recovered.Icon,
            // Older snapshots did not record field ownership. A non-default
            // launcher title was necessarily user-authored; an explicit legacy
            // icon is preserved conservatively rather than overwritten.
            hasChosenTitle: recovered.HasChosenTitle
                ?? !string.Equals(recovered.Title, "New tab", StringComparison.Ordinal),
            hasChosenIcon: recovered.HasChosenIcon ?? recovered.Icon is not null);
        try
        {
            var restoredPanels = new Dictionary<string, RuntimePanelViewModel>(StringComparer.Ordinal);
            for (var index = 0; index < recovered.Panels.Length; index++)
            {
                var recoveredPanel = recovered.Panels[index];
                var panel = RestorePanel(workspaceId, tab.Id, recoveredPanel);
                try
                {
                    tab.AddPanel(
                        panel,
                        recovered.UsesAutomaticLayout ? null : slots[index].Id,
                        recoveredPanel.Key);
                }
                catch
                {
                    panel.Dispose();
                    throw;
                }
                restoredPanels.Add(recoveredPanel.Key, panel);
            }

            if (recovered.ActivePanelKey is { } activePanelKey)
            {
                _ = tab.ActivatePanel(restoredPanels[activePanelKey].Id);
            }
            if (recovered.ZoomedPanelKey is not null)
            {
                _ = tab.ToggleActivePanelZoom();
            }

            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimePanelViewModel RestorePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        RuntimePanelRecoveryPayload recovered)
    {
        if (recovered.Kind == RuntimePanelRecoveryKind.Terminal)
        {
            var connection = recovered.ConnectionId is { } statisticsConnectionId
                ? FindConnection(new ConnectionId(statisticsConnectionId))
                : LocalConnection();
            return connection is null
                ? new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Terminal,
                    recovered.Title,
                    "Terminal",
                    "The recovered terminal connection is no longer available. Repair or recreate the connection, then reopen the panel.")
                : CreateTerminalPanel(
                    workspaceId,
                    tabId,
                    connection,
                    recovered.Title,
                    new PanelStartupBehavior(recovered.StartupLocation),
                    multiplexerSession: recovered.Multiplexer?.ToSession());
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.FileViewer)
        {
            var location = recovered.FileLocation?.ToLocation();
            var profileId = recovered.FileProviderProfileId ?? location?.ProviderProfileId;
            var panel = CreateFilePanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                profileId is null ? null : new FileProviderProfileId(profileId),
                location,
                deferInitialization: true,
                connection: recovered.ConnectionId is { } fileConnectionId
                    ? FindConnection(new ConnectionId(fileConnectionId))
                    : null);
            panel.Filter = recovered.Filter ?? string.Empty;
            panel.ShowHidden = recovered.ShowHidden;
            return panel;
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Browser)
        {
            var connection = recovered.ConnectionId is { } browserConnectionId
                ? FindConnection(new ConnectionId(browserConnectionId))
                : LocalConnection();
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    recovered.Title,
                    "Browser",
                    "The recovered browser connection is no longer available.");
            }

            return BrowserAddress.TryParse(
                recovered.StartupLocation,
                out var address)
                ? CreateBrowserPanel(
                    workspaceId,
                    tabId,
                    PanelInstanceId.New(),
                    recovered.Title,
                    address,
                    connection)
                : new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    recovered.Title,
                    "Browser",
                    "The recovered browser address is invalid.");
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.DatabaseViewer)
        {
            return CreateDatabasePanelFromTarget(
                PanelInstanceId.New(),
                recovered.Title,
                recovered.StartupLocation,
                recovered.ConnectionId is { } tunnelId
                    ? FindConnection(new ConnectionId(tunnelId))
                    : null);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Docker)
        {
            var connection = recovered.ConnectionId is { } dockerConnectionId
                ? FindConnection(new ConnectionId(dockerConnectionId))
                : LocalConnection();
            return connection is null
                ? new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Docker,
                    recovered.Title,
                    "Docker",
                    "The recovered Docker connection is no longer available.")
                : CreateDockerPanel(
                    PanelInstanceId.New(),
                    recovered.Title,
                    connection);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Statistics)
        {
            var connection = recovered.ConnectionId is { } processConnectionId
                ? FindConnection(new ConnectionId(processConnectionId))
                : LocalConnection();
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Statistics,
                    recovered.Title,
                    "Statistics",
                    "The recovered monitoring connection is no longer available.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                PanelKind.Statistics,
                connection);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.ProcessMonitor)
        {
            var connection = FindConnection(new ConnectionId(recovered.ConnectionId!));
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.ProcessMonitor,
                    recovered.Title,
                    "Process monitor",
                    "The recovered monitoring connection is no longer available.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                PanelKind.ProcessMonitor,
                connection);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Placeholder
            || recovered is
            {
                Kind: RuntimePanelRecoveryKind.Unavailable,
                KindLabel: "Choose",
            })
        {
            return new PanelPlaceholderViewModel(PanelInstanceId.New());
        }

        return new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKindFromRecovery(recovered.KindLabel),
            recovered.Title,
            recovered.KindLabel!,
            "This recovered panel type is not available in the current build.");
    }

    private RuntimeTabViewModel CreateRuntimeTab(
        WorkspaceInstanceId workspaceId,
        string title,
        string source,
        LayoutId layoutId,
        IReadOnlyList<ScreenPanelDefinition> panels,
        DefinitionKey? sourceDefinition = null,
        string? durableSourceTitle = null,
        RuntimeAgentPolicyProvenance? agentPolicy = null)
    {
        var layout = _catalog.Snapshot.Layouts
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == layoutId);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            title,
            source,
            layout,
            sourceDefinition is { } key
                && !string.IsNullOrWhiteSpace(durableSourceTitle)
                    ? new RuntimeHistorySource(key, durableSourceTitle)
                    : null,
            agentPolicy);
        try
        {
            foreach (var panel in panels)
            {
                AddPanelOrDispose(
                    tab,
                    CreatePanel(
                        workspaceId,
                        tab.Id,
                        panel,
                        deferFileInitialization: true),
                    panel.SlotId);
            }

            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private static void AddPanelOrDispose(
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel,
        LayoutSlotId? slotId = null)
    {
        try
        {
            tab.AddPanel(panel, slotId);
        }
        catch
        {
            panel.Dispose();
            throw;
        }
    }

    private RuntimePanelViewModel CreatePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        ScreenPanelDefinition panel,
        bool deferFileInitialization = false)
    {
        var title = string.IsNullOrWhiteSpace(panel.Title) ? PanelTitle(panel.Kind) : panel.Title;
        if (panel.Kind == ScreenPanelKind.FileViewer)
        {
            FilePanelLocation? initialLocation = null;
            if (!string.IsNullOrWhiteSpace(panel.Startup.Location))
            {
                var profile = ResolveFileProfile(panel.FileProviderProfileId);
                if (profile is not null)
                {
                    try
                    {
                        initialLocation = FileLocationPresentation.Parse(
                            profile,
                            panel.Startup.Location);
                    }
                    catch (ArgumentException)
                    {
                        return new UnavailableRuntimePanelViewModel(
                            PanelInstanceId.New(),
                            PanelKind.FileViewer,
                            title,
                            "File viewer",
                            "This panel has an invalid startup location. Repair the saved screen with a location supported by its file provider.");
                    }
                }
            }

            return CreateFilePanel(
                    workspaceId,
                    tabId,
                    PanelInstanceId.New(),
                    title,
                    panel.FileProviderProfileId,
                    initialLocation,
                    initialLocation is null ? panel.Startup.Location : null,
                    deferInitialization: deferFileInitialization);
        }

        if (panel.Kind == ScreenPanelKind.Browser)
        {
            if (panel.Startup.Location is { } location
                && !BrowserAddress.TryParse(location, out _))
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    title,
                    "Browser",
                    "This panel has an invalid startup URL. Repair the saved screen with a complete HTTP or HTTPS address.");
            }

            var browserRoute = panel.ConnectionId is { } browserConnectionId
                ? FindConnection(browserConnectionId)
                : LocalConnection();
            if (browserRoute is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    title,
                    "Browser",
                    "The browser connection is no longer in the catalog. Repair the saved screen with a local or SSH connection.");
            }

            return CreateBrowserPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                title,
                panel.Startup.Location is null
                    ? BrowserAddress.Blank
                    : BrowserAddress.TryParse(panel.Startup.Location, out var address)
                        ? address
                        : BrowserAddress.Blank,
                browserRoute);
        }

        if (panel.Kind is ScreenPanelKind.Statistics or ScreenPanelKind.ProcessMonitor)
        {
            // A monitor panel runs its sampler over whichever connection it
            // names, local or not — the same treatment the recovery path has
            // always given it. This branch refused every saved screen that
            // named one, from back when there was nothing behind a remote
            // sampler to run.
            var monitored = panel.ConnectionId is { } monitorConnectionId
                ? FindConnection(monitorConnectionId)
                : null;
            if (panel.ConnectionId is not null && monitored is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKindFromDefinition(panel.Kind),
                    title,
                    panel.Kind == ScreenPanelKind.Statistics
                        ? "Statistics"
                        : "Process monitor",
                    "The connection this panel monitors is no longer in the catalog. "
                    + "Repair the saved screen with a connection that still exists, or remove "
                    + "it from the panel to monitor the local host.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                title,
                PanelKindFromDefinition(panel.Kind),
                monitored);
        }

        if (panel.Kind == ScreenPanelKind.DatabaseViewer)
        {
            return CreateDatabasePanelFromTarget(
                PanelInstanceId.New(),
                title,
                panel.Startup.Location,
                panel.ConnectionId is { } tunnelId ? FindConnection(tunnelId) : null);
        }

        if (panel.Kind == ScreenPanelKind.Docker)
        {
            var dockerConnection = panel.ConnectionId is { } dockerConnectionId
                ? FindConnection(dockerConnectionId)
                : LocalConnection();
            if (dockerConnection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Docker,
                    title,
                    "Docker",
                    "The Docker panel connection is no longer available.");
            }

            return CreateDockerPanel(
                PanelInstanceId.New(),
                title,
                dockerConnection);
        }

        if (panel.Kind != ScreenPanelKind.Terminal)
        {
            return new UnavailableRuntimePanelViewModel(
                PanelInstanceId.New(),
                PanelKindFromDefinition(panel.Kind),
                title,
                KindBadges.Panel(panel.Kind),
                $"{panel.Kind} panels are defined, but their native adapter arrives in a later milestone.");
        }

        var connection = panel.ConnectionId is { } connectionId
            ? FindConnection(connectionId)
            : _catalog.Snapshot.Connections
                .Select(item => item.Value)
                .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (connection is null)
        {
            return new UnavailableRuntimePanelViewModel(
                PanelInstanceId.New(),
                PanelKind.Terminal,
                title,
                "Terminal",
                "This panel references a connection that is not available. Repair the saved screen in Settings.");
        }

        return CreateTerminalPanel(workspaceId, tabId, connection, title, panel.Startup);
    }

    private TerminalRuntimePanelViewModel CreateTerminalPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        ConnectionProfile connection,
        string title,
        PanelStartupBehavior startup,
        PanelInstanceId? panelId = null,
        PanelInstanceId? ownerPanelId = null,
        PanelSessionRole sessionRole = PanelSessionRole.Primary,
        TerminalMultiplexerSession? multiplexerSession = null)
    {
        var resolvedPanelId = panelId ?? PanelInstanceId.New();
        var terminalProfile = ActiveTerminalProfile;
        var terminalKeymap = terminalProfile is null
            ? null
            : ResolveTerminalKeymap(_catalog.Snapshot, terminalProfile.KeymapId);
        var mode = _workspaceTerminalMultiplexingModes.TryGetValue(workspaceId, out var workspaceMode)
            ? workspaceMode
            : _terminalMultiplexingMode;
        var usesContinuity = mode == TerminalMultiplexingMode.Automatic
            && connection.ConnectionKind == ConnectionKind.Ssh;
        if (!usesContinuity)
        {
            // Recovery metadata describes the previous launch, while the
            // current workspace policy decides the next one. Keeping the old
            // identity here silently re-enabled continuity in disabled workspaces.
            multiplexerSession = null;
        }
        else if (multiplexerSession is null)
        {
            multiplexerSession = TerminalMultiplexerSession.CreateAutomatic();
        }

        return new TerminalRuntimePanelViewModel(
            resolvedPanelId,
            title,
            _connectionRuntime,
            connection,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                workspaceId,
                tabId,
                ownerPanelId ?? resolvedPanelId),
            startup,
            terminalProfile is not null
                ? TerminalRenderProfileSnapshot.FromProfile(terminalProfile)
                : null,
            SessionClient,
            ClientId,
            _startupCommandDispatcher,
            _connectionSecurityRuntime,
            keymap: terminalKeymap is null
                ? null
                : TerminalKeymapSnapshot.FromProfile(terminalKeymap),
            sessionRole: sessionRole,
            multiplexerCoordinator: _terminalMultiplexerCoordinator,
            multiplexerSession: multiplexerSession);
    }

    private static KeymapProfile? ResolveTerminalKeymap(
        DefinitionCatalogSnapshot snapshot,
        KeymapProfileId keymapId)
    {
        var stored = snapshot.Keymaps
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Id == keymapId);
        if (stored is not null)
        {
            return stored.Layer == KeymapLayer.Terminal ? stored : null;
        }

        var builtIn = BuiltInKeymaps.All.FirstOrDefault(item => item.Id == keymapId);
        return builtIn?.Layer == KeymapLayer.Terminal ? builtIn : null;
    }

    private FileRuntimePanelViewModel CreateFilePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        FileProviderProfileId? initialProfileId = null,
        FilePanelLocation? initialLocation = null,
        string? initialLocationText = null,
        bool deferInitialization = false,
        ConnectionProfile? connection = null)
    {
        connection ??= ConnectionForFileProfile(initialProfileId) ?? LocalConnection();
        var profile = ResolveFileProfile(initialProfileId);
        var hostInitialLocation = initialLocation ?? profile?.Root;
        var owner = new SessionOwner(
            HostMode.Desktop,
            WindowId,
            workspaceId,
            tabId,
            panelId);
        var options = hostInitialLocation is null
            ? HostedFilePanelClientOptions.Deferred(
                SessionId.New(),
                owner,
                ClientId,
                title,
                initialProfileId)
            : new HostedFilePanelClientOptions(
                SessionId.New(),
                owner,
                ClientId,
                title,
                hostInitialLocation);
        var hostedClient = new SessionHostedFilePanelClient(
            SessionClient,
            _filePanelClient,
            options,
            _fileTransferQueue);
        return new FileRuntimePanelViewModel(
            panelId,
            title,
            hostedClient,
            hostedClient,
            initialProfileId,
            initialLocation,
            initialLocationText,
            deferInitialization,
            connection,
            _databasePanelClient,
            _imagePreviewDecoder,
            _pdfPreviewRenderer,
            _archiveTableOfContents,
            previewers: null,
            _inMemoryDatabaseRegistry,
            _filePreviewPreferences,
            FileTransferClipboard);
    }

    private FileProviderProfileDescriptor? ResolveFileProfile(
        FileProviderProfileId? profileId) =>
        profileId is { } requestedProfile
            ? _filePanelClient.Profiles.FirstOrDefault(
                item => item.Id == requestedProfile.Value)
            : _filePanelClient.Profiles.FirstOrDefault(
                    item => item.Id == BuiltInFileProviders.HomeId.Value)
                ?? _filePanelClient.Profiles.FirstOrDefault();

    private RuntimePanelViewModel CreateBrowserPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        BrowserAddress initialAddress,
        ConnectionProfile? connection = null)
    {
        if (_browserRendererViewFactory is null)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Browser,
                title,
                "Browser",
                "The embedded browser is unavailable in this build.");
        }

        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Browser,
                title,
                "Browser",
                "This browser route is not a local or SSH connection.");
        }

        return new BrowserRuntimePanelViewModel(
            panelId,
            title,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                workspaceId,
                tabId,
                panelId),
            initialAddress,
            SessionClient,
            ClientId,
            connection,
            _browserRendererViewFactory);
    }

    private RuntimePanelViewModel CreateMonitorPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        PanelKind kind,
        ConnectionProfile? connection = null)
    {
        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        var owner = new SessionOwner(
            HostMode.Desktop,
            WindowId,
            workspaceId,
            tabId,
            panelId);
        return kind switch
        {
            PanelKind.Statistics => new StatisticsRuntimePanelViewModel(
                panelId,
                title,
                SessionClient,
                ClientId,
                owner,
                connection,
                _uiThreadDispatcher),
            PanelKind.ProcessMonitor => new ProcessMonitorRuntimePanelViewModel(
                panelId,
                title,
                SessionClient,
                ClientId,
                owner,
                connection,
                _uiThreadDispatcher),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// A database panel needs no hosted session: queries run through the
    /// application-level client, so the panel is unavailable only when the
    /// desktop composition did not provide one.
    /// </summary>
    private RuntimePanelViewModel CreateDatabasePanel(
        PanelInstanceId panelId,
        string title,
        string? driverId = null,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        DatabaseObjectId? initialObject = null) =>
        _databasePanelClient is null
            ? new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.DatabaseViewer,
                title,
                "Database",
                "The database drivers are unavailable in this build.")
            : new DatabaseRuntimePanelViewModel(
                panelId,
                title,
                _databasePanelClient,
                driverId,
                connectionString,
                tunnelConnection,
                savedConnection,
                ResolveDatabasePasswordAsync,
                initialObject: initialObject,
                sqlLanguageService: _sqlLanguageService,
                passwordPersister: _secretVault.Availability.CanPersist
                    ? StoreDatabasePasswordAsync
                    : null,
                passwordStoreLabel: DatabasePasswordStoreLabel(
                    _secretVault.Availability.Adapter));

    private static string DatabasePasswordStoreLabel(string adapter) => adapter switch
    {
        "macos-keychain" => "Save in macOS Keychain",
        "windows-dpapi" => "Save securely for this Windows user",
        "linux-secret-service" => "Save in Secret Service",
        _ => "Save in system credential store",
    };

    private RuntimePanelViewModel CreateDockerPanel(
        PanelInstanceId panelId,
        string title,
        ConnectionProfile? connection = null)
    {
        if (_dockerEngineClient is null)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Docker,
                title,
                "Docker",
                "Docker support is unavailable in this build.");
        }

        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Docker,
                title,
                "Docker",
                "Docker panels can use only a local or SSH connection.");
        }

        return new DockerRuntimePanelViewModel(
            panelId,
            title,
            _dockerEngineClient,
            connection);
    }

    /// <summary>
    /// Opens an interactive shell in a new tab. The terminal uses
    /// the same local or SSH connection as the panel, then sends one quoted
    /// docker-exec command through the normal audited startup-command path.
    /// </summary>
    public async Task<bool> OpenDockerContainerShellAsync(
        DockerRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClearError();
        if (source.SelectedResource?.Container is not { IsRunning: true } container)
        {
            SetError("Select a running container before opening a shell.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null
            || workspace.Tabs.All(candidate => !candidate.Panels.Contains(source)))
        {
            SetError("That Docker panel is no longer open.");
            return false;
        }

        var shellPath = await ResolveDockerContainerShellAsync(
            source,
            container.Id,
            cancellationToken);
        if (shellPath is null
            || !ReferenceEquals(RuntimeWorkspace, workspace)
            || workspace.Tabs.All(candidate => !candidate.Panels.Contains(source)))
        {
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    $"{container.Name} shell",
                    source.ConnectionDisplayName,
                    historySource: new RuntimeHistorySource(
                        source.Connection.Key,
                        source.Connection.Name));
                AddPanelOrDispose(
                    tab,
                    CreateTerminalPanel(
                        runtime.Id,
                        tab.Id,
                        source.Connection,
                        $"{container.Name} shell",
                        new PanelStartupBehavior(
                            commands:
                            [
                                DockerContainerShellCommand.Build(
                                    container.Id,
                                    shellPath),
                            ])));
                return tab;
            },
            "container shell tab creation",
            cancellationToken);
    }

    /// <summary>
    /// Hosts a container shell inside its Docker inspector. The terminal view model has its own
    /// presentation identity, while the hosted session is owned by the Docker panel so panel/tab/
    /// window close scopes still discover and close it.
    /// </summary>
    public async Task<bool> OpenDockerContainerInlineShellAsync(
        DockerRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ClearError();
        if (source.HasInlineShell)
        {
            return true;
        }

        if (source.SelectedResource?.Container is not { IsRunning: true } container)
        {
            SetError("Select a running container before opening a shell.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.FirstOrDefault(candidate => candidate.Panels.Contains(source));
        if (workspace is null || tab is null)
        {
            SetError("That Docker panel is no longer open.");
            return false;
        }

        var shellPath = await ResolveDockerContainerShellAsync(
            source,
            container.Id,
            cancellationToken);
        if (shellPath is null
            || !ReferenceEquals(RuntimeWorkspace, workspace)
            || !tab.Panels.Contains(source)
            || source.SelectedResource?.Container?.Id != container.Id)
        {
            return false;
        }

        if (source.HasInlineShell)
        {
            return true;
        }

        var shell = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            source.Connection,
            $"{container.Name} shell",
            new PanelStartupBehavior(
                commands:
                [
                    DockerContainerShellCommand.Build(container.Id, shellPath),
                ]),
            ownerPanelId: source.Id,
            sessionRole: PanelSessionRole.Embedded);
        source.AttachInlineShell(container.Id, shell);
        return true;
    }

    private async Task<string?> ResolveDockerContainerShellAsync(
        DockerRuntimePanelViewModel source,
        string containerId,
        CancellationToken cancellationToken)
    {
        source.BeginShellResolution(containerId);
        if (_dockerEngineClient is null)
        {
            source.PresentShellResolutionFailure(
                containerId,
                new DockerError(
                    DockerErrorCode.RuntimeUnavailable,
                    "Docker support is unavailable in this build.",
                    false));
            return null;
        }

        var result = await _dockerEngineClient.ResolveContainerShellAsync(
            source.Connection,
            containerId,
            cancellationToken);
        if (result is DockerResult<string>.Success success)
        {
            source.CompleteShellResolution(containerId);
            return success.Value;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            source.PresentShellResolutionFailure(
                containerId,
                ((DockerResult<string>.Failure)result).Error);
        }

        return null;
    }

    /// <summary>
    /// A twin of a live database panel: same connection, tunnel and all,
    /// opening straight onto one object — or, with no object, onto the whole
    /// database. The twin binds the same saved profile when there is one, so
    /// passwords resolve the same way.
    /// </summary>
    private RuntimePanelViewModel CreateDatabaseTwinPanel(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor? databaseObject)
    {
        var title = databaseObject?.DisplayName ?? source.Title;
        if (source.SavedConnectionId is { } profileId
            && FindDatabaseConnection(profileId) is { } profile)
        {
            return CreateDatabasePanel(
                PanelInstanceId.New(),
                title,
                tunnelConnection: ResolveDatabaseTunnel(profile),
                savedConnection: profile,
                initialObject: databaseObject?.Id);
        }

        return CreateDatabasePanel(
            PanelInstanceId.New(),
            title,
            source.SelectedDriver.Id,
            source.ConnectionString,
            source.TunnelConnection,
            initialObject: databaseObject?.Id);
    }

    /// <summary>
    /// Opens the same database as a full viewer tab — the embedded preview's
    /// way out into the real thing, without the preview's read-only leash.
    /// </summary>
    public async Task<bool> OpenDatabaseInTabAsync(
        DatabaseRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before opening a database tab.");
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            _ =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    source.Title,
                    "Database");
                AddPanelOrDispose(tab, CreateDatabaseTwinPanel(source, databaseObject: null));
                return tab;
            },
            "database viewer tab creation",
            cancellationToken);
    }

    /// <summary>Opens an object from a live panel as its own tab, same connection.</summary>
    public async Task<bool> OpenDatabaseObjectInTabAsync(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(databaseObject);
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before opening an object in a tab.");
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            _ =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    databaseObject.DisplayName,
                    "Database");
                AddPanelOrDispose(tab, CreateDatabaseTwinPanel(source, databaseObject));
                return tab;
            },
            "database object tab creation",
            cancellationToken);
    }

    /// <summary>Opens an object beside its panel — the split with a purpose.</summary>
    public async Task<bool> OpenDatabaseObjectInPanelAsync(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(databaseObject);
        ClearError();
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.FirstOrDefault(candidate =>
                candidate.Panels.Contains(source))
            ?? workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before opening an object in a panel.");
            return false;
        }

        var panel = CreateDatabaseTwinPanel(source, databaseObject);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "panel split",
            () =>
            {
                _ = tab.SplitActivePanel(panel, PanelSplitOrientation.LeftRight);
                StartTrackingRecovery(panel);
            },
            cancellationToken);
    }

    private const string SavedDatabaseTargetPrefix = "saved:";

    /// <summary>
    /// Builds a database panel from a durable target: "saved:{profile id}"
    /// binds a saved connection (its own tunnel wins over the recovered one),
    /// anything else is a raw "driver:connection string" address.
    /// </summary>
    private RuntimePanelViewModel CreateDatabasePanelFromTarget(
        PanelInstanceId panelId,
        string title,
        string? target,
        ConnectionProfile? recoveredTunnel)
    {
        if (target?.StartsWith(SavedDatabaseTargetPrefix, StringComparison.Ordinal) == true)
        {
            var profileId = target[SavedDatabaseTargetPrefix.Length..];
            var stored = _catalog.Snapshot.DatabaseConnections
                .SingleOrDefault(item => item.Value.Id.Value == profileId);
            if (stored is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    panelId,
                    PanelKind.DatabaseViewer,
                    title,
                    "Database",
                    "The saved database connection no longer exists.");
            }

            var profile = stored.Value;
            var tunnel = ResolveDatabaseTunnel(profile) ?? recoveredTunnel;
            return CreateDatabasePanel(
                panelId,
                title,
                tunnelConnection: tunnel,
                savedConnection: profile);
        }

        var parsed = DatabasePanelTarget.TryParse(target);
        return CreateDatabasePanel(
            panelId,
            title,
            parsed?.DriverId,
            parsed?.ConnectionString,
            recoveredTunnel);
    }

    /// <summary>
    /// The tunnel a database profile connects through: a saved SSH connection
    /// when referenced, else the profile's own inline tunnel.
    /// </summary>
    public ConnectionProfile? ResolveDatabaseTunnel(DatabaseConnectionProfile profile) =>
        profile.TunnelConnectionId is { } tunnelId
            ? FindConnection(tunnelId)
            : profile.InlineTunnel;

    /// <summary>
    /// Rebinds a live database panel to a saved connection, tunnel and all.
    /// The panel is stateless between operations, so nothing is torn down —
    /// it simply connects to the new target.
    /// </summary>
    public bool ReplaceDatabasePanelConnection(
        DatabaseRuntimePanelViewModel panel,
        DatabaseConnectionProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ClearError();
        var profile = FindDatabaseConnection(profileId);
        if (profile is null)
        {
            SetError("That database connection no longer exists.");
            return false;
        }

        panel.ApplySavedConnection(profile, tunnel: ResolveDatabaseTunnel(profile));
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    /// <summary>
    /// The editor's "connect without saving": the request becomes an
    /// in-memory profile the panel binds to. The typed password becomes the
    /// session password; nothing reaches the catalog or the vault — which is
    /// also why an inline tunnel needing a stored password must be saved.
    /// </summary>
    public bool BindUnsavedDatabaseConnection(
        DatabaseRuntimePanelViewModel panel,
        DatabaseConnectionSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        if (_databasePanelClient is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        ConnectionProfile? tunnel = null;
        if (request.TunnelConnectionId is { } tunnelId)
        {
            tunnel = FindConnection(tunnelId);
            if (tunnel is null)
            {
                SetError("That SSH tunnel connection no longer exists.");
                return false;
            }
        }
        else if (request.InlineTunnel is { } inline)
        {
            if (!inline.UseAgent)
            {
                SetError(
                    "Tunnel passwords live in the OS keychain. "
                    + "Save the connection to use a password tunnel.");
                return false;
            }

            tunnel = DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
                DatabaseConnectionProfile.InlineTunnelId(DatabaseConnectionProfileId.New()),
                $"{request.Name} tunnel",
                inline,
                new ConnectionAuthentication.SshAgent());
        }

        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            request.Name,
            request.DriverId,
            _databasePanelClient.BuildConnectionString(
                request.DriverId,
                request.Details with { Password = null }));
        panel.ApplySavedConnection(
            profile,
            request.Details.Password,
            tunnel,
            persisted: false);
        return true;
    }

    /// <summary>Every saved database connection, for panel pickers.</summary>
    public IReadOnlyList<DatabaseConnectionProfile> DatabaseConnectionOptions =>
        _catalog.Snapshot.DatabaseConnections.Select(item => item.Value).ToArray();

    public DatabaseConnectionProfile? FindDatabaseConnection(DatabaseConnectionProfileId id) =>
        _catalog.Snapshot.DatabaseConnections
            .SingleOrDefault(item => item.Value.Id == id)?.Value;

    /// <summary>
    /// Persists a database connection, optionally moving the typed password
    /// into the OS vault. The stored connection string never carries the
    /// password. Updates keep the existing profile id and its stored secret
    /// unless a new password is being stored.
    /// </summary>
    public async Task<DatabaseConnectionProfile?> SaveDatabaseConnectionAsync(
        DatabaseConnectionProfileId? existingId,
        string name,
        string driverId,
        DatabaseConnectionDetails details,
        bool storePassword,
        ConnectionId? tunnelConnectionId,
        DatabaseInlineTunnelRequest? inlineTunnel = null,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_databasePanelClient is null || string.IsNullOrWhiteSpace(name))
        {
            SetError("A saved database connection needs a name.");
            return null;
        }

        var existing = existingId is { } id
            ? _catalog.Snapshot.DatabaseConnections
                .SingleOrDefault(item => item.Value.Id == id)
            : null;
        var profileId = existing?.Value.Id ?? DatabaseConnectionProfileId.New();
        var secret = existing?.Value.PasswordSecret;
        if (storePassword && !string.IsNullOrEmpty(details.Password))
        {
            var reference = SecretRef.New();
            var bytes = Encoding.UTF8.GetBytes(details.Password);
            using var material = SecretMaterial.TakeOwnership(bytes);
            var created = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    reference,
                    $"{name.Trim()} database password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.DatabaseConnection, profileId.Value),
                    new SecretUsePurpose(
                        SecretUseKind.DatabaseConnectionAuthentication,
                        profileId.Value)),
                material,
                cancellationToken);
            if (created is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                SetError(failure.Error.Message);
                return null;
            }

            secret = reference;
        }

        ConnectionProfile? inline = null;
        if (inlineTunnel is { } tunnelRequest)
        {
            inline = await BuildInlineTunnelAsync(
                profileId,
                name.Trim(),
                tunnelRequest,
                existing?.Value.InlineTunnel,
                cancellationToken);
            if (inline is null)
            {
                return null;
            }
        }

        var profile = new DatabaseConnectionProfile(
            profileId,
            DatabaseConnectionProfile.CurrentSchemaVersion,
            name.Trim(),
            driverId,
            _databasePanelClient.BuildConnectionString(
                driverId,
                details with { Password = null }),
            secret,
            inline is null ? tunnelConnectionId : null,
            inline);
        var saved = await _catalog.SaveDatabaseConnectionAsync(
            profile,
            existing?.Revision,
            cancellationToken);
        if (!saved.IsSuccess)
        {
            SetError(saved.Error!.Message);
            return null;
        }

        OnPropertyChanged(nameof(DatabaseConnectionOptions));
        OnPropertyChanged(nameof(DatabasePanelConnectionOptions));
        return saved.Value!.Value;
    }

    /// <summary>
    /// Adds a password supplied by the connection-time prompt to an existing
    /// database profile. If the catalog update fails, the newly-created,
    /// unreferenced vault entry is removed before returning.
    /// </summary>
    internal async Task<DatabaseConnectionProfile?> StoreDatabasePasswordAsync(
        DatabaseConnectionProfileId profileId,
        string password,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (!_secretVault.Availability.CanPersist)
        {
            SetError(_secretVault.Availability.Message);
            return null;
        }

        if (string.IsNullOrEmpty(password))
        {
            SetError("Enter a password before saving it.");
            return null;
        }

        var stored = _catalog.Snapshot.DatabaseConnections
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            SetError("That database connection no longer exists.");
            return null;
        }

        if (stored.Value.PasswordSecret is not null)
        {
            return stored.Value;
        }

        var reference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.DatabaseConnection, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.DatabaseConnectionAuthentication,
            profileId.Value);
        var bytes = Encoding.UTF8.GetBytes(password);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var created = await _secretVault.CreateAsync(
            new CreateSecretRequest(
                reference,
                $"{stored.Value.Name} database password",
                SecretKind.Password,
                scope,
                purpose),
            material,
            cancellationToken);
        if (created is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            SetError(failure.Error.Message);
            return null;
        }

        var profile = new DatabaseConnectionProfile(
            stored.Value.Id,
            stored.Value.SchemaVersion,
            stored.Value.Name,
            stored.Value.DriverId,
            stored.Value.ConnectionString,
            reference,
            stored.Value.TunnelConnectionId,
            stored.Value.InlineTunnel);
        DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>> saved;
        try
        {
            saved = await _catalog.SaveDatabaseConnectionAsync(
                profile,
                stored.Revision,
                cancellationToken);
        }
        catch
        {
            await DeleteUnusedDatabasePasswordAsync(reference, scope, profileId);
            throw;
        }

        if (!saved.IsSuccess)
        {
            await DeleteUnusedDatabasePasswordAsync(reference, scope, profileId);
            SetError(saved.Error!.Message);
            return null;
        }

        OnPropertyChanged(nameof(DatabaseConnectionOptions));
        OnPropertyChanged(nameof(DatabasePanelConnectionOptions));
        return saved.Value!.Value;
    }

    private async Task DeleteUnusedDatabasePasswordAsync(
        SecretRef reference,
        SecretScope scope,
        DatabaseConnectionProfileId profileId)
    {
        var deleted = await _secretVault.DeleteAsync(
            new DeleteSecretRequest(
                reference,
                scope,
                new SecretUsePurpose(SecretUseKind.UserManagement, profileId.Value)),
            CancellationToken.None);
        if (deleted is SecretVaultResult<Unit>.Failure)
        {
            SecretVaultStatus =
                "An unused database credential could not be removed from the system credential store.";
        }
    }

    /// <summary>
    /// Turns the editor's inline-tunnel request into the profile stored inside
    /// the database connection. The tunnel's id derives from the database
    /// profile so its keychain password stays scoped and resolvable across
    /// edits; a request with no new password keeps the stored one.
    /// </summary>
    private async Task<ConnectionProfile?> BuildInlineTunnelAsync(
        DatabaseConnectionProfileId profileId,
        string name,
        DatabaseInlineTunnelRequest request,
        ConnectionProfile? existingInline,
        CancellationToken cancellationToken)
    {
        var tunnelId = DatabaseConnectionProfile.InlineTunnelId(profileId);
        ConnectionAuthentication authentication;
        if (request.UseAgent)
        {
            authentication = new ConnectionAuthentication.SshAgent();
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            var reference = SecretRef.New();
            var bytes = Encoding.UTF8.GetBytes(request.Password);
            using var material = SecretMaterial.TakeOwnership(bytes);
            var created = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    reference,
                    $"{name} tunnel password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.Connection, tunnelId.Value),
                    new SecretUsePurpose(
                        SecretUseKind.ConnectionAuthentication,
                        tunnelId.Value)),
                material,
                cancellationToken);
            if (created is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                SetError(failure.Error.Message);
                return null;
            }

            authentication = new ConnectionAuthentication.Password(reference);
        }
        else if (existingInline?.Authentication is ConnectionAuthentication.Password kept)
        {
            authentication = kept;
        }
        else
        {
            SetError("The SSH tunnel needs a password, or switch it to the SSH agent.");
            return null;
        }

        return DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
            tunnelId,
            $"{name} tunnel",
            request,
            authentication);
    }

    /// <summary>Resolves a stored database password from the OS vault.</summary>
    private async Task<string?> ResolveDatabasePasswordAsync(
        SecretRef secret,
        CancellationToken cancellationToken)
    {
        var owner = _catalog.Snapshot.DatabaseConnections
            .FirstOrDefault(item => item.Value.PasswordSecret == secret)?.Value;
        if (owner is null)
        {
            return null;
        }

        var result = await _secretVault.ResolveAsync(
            new ResolveSecretRequest(
                secret,
                new SecretScope(SecretScopeKind.DatabaseConnection, owner.Id.Value),
                new SecretUsePurpose(
                    SecretUseKind.DatabaseConnectionAuthentication,
                    owner.Id.Value)),
            cancellationToken);
        if (result is SecretVaultResult<SecretMaterial>.Failure)
        {
            return null;
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void StartAcceptedRuntimePanels(RuntimeWorkspaceViewModel runtime)
    {
        foreach (var panel in runtime.Tabs.SelectMany(tab => tab.Panels))
        {
            StartAcceptedRuntimePanel(panel);
        }
    }

    private void StartAcceptedRuntimePanel(RuntimePanelViewModel panel)
    {
        if (panel is FileRuntimePanelViewModel files)
        {
            _ = files.StartInitialization();
        }

        if (panel is BrowserRuntimePanelViewModel browser)
        {
            _ = TrackBrowserAfterInitializationAsync(
                browser,
                browser.StartInitialization());
        }

        StartMonitorPanel(panel);
    }

    private async Task TrackBrowserAfterInitializationAsync(
        BrowserRuntimePanelViewModel panel,
        Task initialization)
    {
        try
        {
            await initialization;
        }
        catch (OperationCanceledException) when (_shutdownStarted)
        {
        }
        catch (Exception exception)
        {
            if (!panel.HasRouteError)
            {
                SetError(exception.Message);
                return;
            }

            SetError(panel.RouteErrorMessage!);
        }
    }

    private void StartMonitorPanel(RuntimePanelViewModel panel)
    {
        var initialization = panel switch
        {
            StatisticsRuntimePanelViewModel statistics => statistics.Start(),
            ProcessMonitorRuntimePanelViewModel processes => processes.Start(),
            _ => null,
        };
        if (initialization is not null)
        {
            _ = TrackMonitorAfterInitializationAsync(panel, initialization);
        }
    }

    private async Task TrackMonitorAfterInitializationAsync(
        RuntimePanelViewModel panel,
        Task initialization)
    {
        try
        {
            await initialization;
            var hosted = panel switch
            {
                StatisticsRuntimePanelViewModel statistics => statistics.HasHostedSession,
                ProcessMonitorRuntimePanelViewModel processes => processes.HasHostedSession,
                _ => false,
            };
            if (hosted && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                await _uiThreadDispatcher.InvokeAsync(
                    () => TrackRecentSession(panel),
                    _runtimeGraphLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
        }
    }

    private IReadOnlyList<LauncherConnectionViewModel> ResolveWorkspaceConnections(
        WorkspaceDefinition workspace)
    {
        var ids = ResolveWorkspaceConnectionDefinitions(workspace)
            .Select(item => item.Id)
            .ToHashSet();
        return Connections.Where(item => ids.Contains(item.Id)).ToArray();
    }

    private IEnumerable<ConnectionProfile> ResolveWorkspaceConnectionDefinitions(
        WorkspaceDefinition workspace)
    {
        var directIds = workspace.Entries
            .OfType<WorkspaceEntry.ConnectionReference>()
            .Select(item => item.ConnectionId);
        var screenIds = workspace.Entries
            .OfType<WorkspaceEntry.ScreenReference>()
            .Select(item => item.ScreenId)
            .ToHashSet();
        var panelConnectionIds = _catalog.Snapshot.Screens
            .Where(item => screenIds.Contains(item.Value.Id))
            .SelectMany(item => item.Value.Panels)
            .Select(item => item.ConnectionId)
            .OfType<ConnectionId>();
        var workspaceTabConnectionIds = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .SelectMany(item => item.Panels)
            .Select(item => item.ConnectionId)
            .OfType<ConnectionId>();
        var ids = directIds
            .Concat(panelConnectionIds)
            .Concat(workspaceTabConnectionIds)
            .ToHashSet();
        return _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .Where(item => ids.Contains(item.Id));
    }

    private ConnectionProfile? FindConnection(ConnectionId id) => _catalog.Snapshot.Connections
        .Select(item => item.Value)
        .SingleOrDefault(item => item.Id == id);

    private ConnectionProfile? LocalConnection() => _catalog.Snapshot.Connections
        .Select(item => item.Value)
        .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);

    private ConnectionProfile? ConnectionForFileProfile(FileProviderProfileId? profileId)
    {
        if (profileId is null || profileId == BuiltInFileProviders.HomeId)
        {
            return LocalConnection();
        }

        return _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .Where(item => item.Endpoint is ConnectionEndpoint.Ssh)
            .FirstOrDefault(item =>
                ConnectionFileProviderProfiles.Id(item.Id) == profileId.Value);
    }

    private const int DefaultSshPort = 22;

    private const int ScreenSummaryConnectionLimit = 2;

    /// <summary>
    /// The saved-screen card reads "3 panels · prod-api, staging-web": the panel
    /// count plus the distinct connections it opens, so the card says what the
    /// screen touches without repeating the layout's internal name.
    /// </summary>
    private static string ScreenSummary(
        ScreenDefinition screen,
        DefinitionCatalogSnapshot snapshot)
    {
        var panels = $"{screen.Panels.Count} {(screen.Panels.Count == 1 ? "panel" : "panels")}";
        var namesById = snapshot.Connections.ToDictionary(
            item => item.Value.Id,
            item => item.Value.Name);
        var connections = screen.Panels
            .Select(panel => panel.ConnectionId)
            .OfType<ConnectionId>()
            .Distinct()
            .Select(id => namesById.TryGetValue(id, out var name) ? name : id.Value)
            .ToArray();

        if (connections.Length == 0)
        {
            return panels;
        }

        var shown = string.Join(", ", connections.Take(ScreenSummaryConnectionLimit));
        var remaining = connections.Length - ScreenSummaryConnectionLimit;
        return remaining > 0
            ? $"{panels} · {shown} +{remaining}"
            : $"{panels} · {shown}";
    }

    private static LauncherConnectionViewModel ToConnectionItem(
        ConnectionProfile connection,
        long revision)
    {
        var (detail, canOpen) = connection.Endpoint switch
        {
            ConnectionEndpoint.Local local =>
                (local.ShellPath ?? "Default local shell", true),
            ConnectionEndpoint.Ssh ssh => (
                ssh.Port == DefaultSshPort
                    ? $"{ssh.Username ?? "user"}@{ssh.Host}"
                    : $"{ssh.Username ?? "user"}@{ssh.Host}:{ssh.Port}",
                true),
            ConnectionEndpoint.Docker docker => ($"Container {docker.Container}", true),
            ConnectionEndpoint.Wsl wsl => ($"Distribution {wsl.Distribution}", OperatingSystem.IsWindows()),
            _ => ("Unsupported endpoint", false),
        };
        return new(
            connection.Id,
            revision,
            connection.Name,
            KindBadges.Connection(connection.ConnectionKind),
            detail,
            canOpen ? "Validated on open" : "Unavailable on this platform",
            canOpen,
            connection.Tags);
    }

    private LauncherConnectionViewModel ToFileConnectionItem(
        FileProviderProfile profile,
        long revision) =>
        new(
            new ConnectionId(profile.Id.Value),
            revision,
            profile.Name,
            FileProviderKindLabel(profile.ProviderKind),
            FileProviderEndpoint(profile.Configuration),
            "Validated on open",
            _fileProviderRuntime is not null,
            [],
            SavedConnectionFamily.Files,
            profile.Id.Value);

    private LauncherConnectionViewModel ToDatabaseConnectionItem(
        DatabaseConnectionProfile profile,
        long revision)
    {
        var driver = _databasePanelClient?.Drivers
            .FirstOrDefault(item => item.Id == profile.DriverId);
        return new(
            new ConnectionId(profile.Id.Value),
            revision,
            profile.Name,
            driver?.DisplayName ?? profile.DriverId,
            DatabaseConnectionDetailText(profile),
            _databasePanelClient is null
                ? "Database drivers are unavailable in this build"
                : "Validated on connect",
            _databasePanelClient is not null,
            [],
            SavedConnectionFamily.Database,
            profile.Id.Value);
    }

    /// <summary>
    /// A compact endpoint summary for the card. The stored connection string
    /// never contains the password, so falling back to it verbatim is safe.
    /// </summary>
    private string DatabaseConnectionDetailText(DatabaseConnectionProfile profile)
    {
        if (_databasePanelClient is null)
        {
            return profile.DriverId;
        }

        try
        {
            var details = _databasePanelClient.ParseConnectionDetails(
                profile.DriverId,
                profile.ConnectionString);
            if (details.FilePath is { } filePath)
            {
                return filePath;
            }

            var host = details.Host ?? "localhost";
            var endpoint = details.Port is { } port
                ? $"{host}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : host;
            return details.Database is { } database
                ? $"{endpoint}/{database}"
                : endpoint;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return profile.ConnectionString;
        }
    }

    private static string FileProviderEndpoint(FileProviderConfiguration configuration) =>
        configuration switch
        {
            FileProviderConfiguration.Local value => value.RootPath,
            FileProviderConfiguration.S3 value => value.ServiceUri is null
                ? $"s3://{value.BucketName} · {value.Region ?? "us-east-1"}"
                : $"{value.ServiceUri.Host} · {value.BucketName}",
            FileProviderConfiguration.Sftp value =>
                $"SSH connection {value.ConnectionId.Value} · {value.RemoteRoot}",
            FileProviderConfiguration.Ftp value =>
                $"{value.Security} · {value.Host}:{value.Port}{value.RemoteRoot}",
            FileProviderConfiguration.Smb value =>
                $"smb://{value.Server}/{value.Share}{value.RemoteRoot}",
            FileProviderConfiguration.WebDav value => value.BaseUri.AbsoluteUri,
            _ => "Unsupported provider",
        };

    private static string FileProviderKindLabel(FileProviderKind kind) => kind switch
    {
        FileProviderKind.Local => "Local",
        FileProviderKind.S3 => "S3",
        FileProviderKind.Sftp => "SFTP",
        FileProviderKind.Ftp => "FTP/FTPS",
        FileProviderKind.Smb => "SMB",
        FileProviderKind.WebDav => "WebDAV",
        _ => kind.ToString().ToUpperInvariant(),
    };

    private static string FileProviderFamilyLabel(FileProviderFamily family) => family switch
    {
        FileProviderFamily.Posix or FileProviderFamily.Windows => "Local",
        FileProviderFamily.S3 => "S3",
        FileProviderFamily.Sftp => "SFTP",
        FileProviderFamily.Ftp => "FTP/FTPS",
        FileProviderFamily.Smb => "SMB",
        FileProviderFamily.WebDav => "WebDAV",
        _ => family.ToString().ToUpperInvariant(),
    };

    private static string FileProviderDetail(FileProviderProfileDescriptor profile)
    {
        var root = FileLocationPresentation.Display(profile.Root);
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.IsNullOrWhiteSpace(profile.Root.Authority)
                ? "Configured root"
                : profile.Root.Authority;
        }

        return string.IsNullOrWhiteSpace(profile.Root.Authority)
            ? root
            : $"{profile.Root.Authority} · {root}";
    }

    public Task QuiesceForShutdownAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_shutdownGate)
        {
            _shutdownStarted = true;
            _shutdownTask ??= QuiesceForShutdownCoreAsync();
            shutdown = _shutdownTask;
        }

        return shutdown.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Releases presentation-owned runtimes while Avalonia still owns its UI
    /// thread. The desktop lifetime calls this before its asynchronous shutdown
    /// finalizer; repeated calls are safe so the main thread can provide a
    /// fallback after the lifetime returns.
    /// </summary>
    public void TeardownPresentationForShutdown()
    {
        _uiThreadDispatcher.VerifyAccess();
        if (_presentationTeardownCompleted)
        {
            return;
        }

        _shutdownStarted = true;
        StopTrackingAgentTerminalSelection(_runtimeWorkspace);
        StopTrackingRecovery(_runtimeWorkspace);
        QueueRemainingRecentSessionCompletions(RecentSessionOutcome.GracefullyClosed);

        var openWorkspaces = _openWorkspaces.ToArray();
        foreach (var workspace in openWorkspaces)
        {
            workspace.DisposePanels();
        }

        // The active workspace normally belongs to OpenWorkspaces. Retain a
        // defensive path for a partially completed open/restore operation.
        if (_runtimeWorkspace is { } activeWorkspace
            && !openWorkspaces.Contains(activeWorkspace))
        {
            activeWorkspace.DisposePanels();
        }

        _presentationTeardownCompleted = true;
    }

    private async Task QuiesceForShutdownCoreAsync()
    {
        if (AgentChat is not null)
        {
            await AgentChat.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
            AgentChat.Dispose();
        }

        _catalog.Changed -= OnCatalogChanged;
        _fileTransferQueue.TransfersChanged -= OnFileTransfersChanged;
        if (_fileProviderRuntime is not null)
        {
            _fileProviderRuntime.ProfilesChanged -= OnFileProviderProfilesChanged;
        }
        if (_aiProviderRuntime is not null)
        {
            _aiProviderRuntime.ProfilesChanged -= OnAiProviderProfilesChanged;
        }
        if (_runtimeRecoveryWriter is not null)
        {
            _runtimeRecoveryWriter.WriteFailed -= OnRuntimeRecoveryWriteFailed;
        }
        if (_terminalMultiplexerCoordinator is not null)
        {
            _terminalMultiplexerCoordinator.LeasesChanged -=
                OnTerminalMultiplexerLeasesChanged;
        }

        lock (_historyGate)
        {
            _historyOperationsSealed = true;
        }

        _runtimeGraphLifetime.Cancel();
        StopRuntimeGraphWatch();

        Task[] graphWatches;
        lock (_shutdownGate)
        {
            graphWatches = _runtimeGraphWatchTasks.ToArray();
        }

        await Task.WhenAll(graphWatches).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownStarted = true;
        lock (_historyGate)
        {
            _historyOperationsSealed = true;
        }

        _catalog.Changed -= OnCatalogChanged;
        _fileTransferQueue.TransfersChanged -= OnFileTransfersChanged;
        if (_fileProviderRuntime is not null)
        {
            _fileProviderRuntime.ProfilesChanged -= OnFileProviderProfilesChanged;
        }
        if (_aiProviderRuntime is not null)
        {
            _aiProviderRuntime.ProfilesChanged -= OnAiProviderProfilesChanged;
        }
        if (_runtimeRecoveryWriter is not null)
        {
            _runtimeRecoveryWriter.WriteFailed -= OnRuntimeRecoveryWriteFailed;
        }
        if (_terminalMultiplexerCoordinator is not null)
        {
            _terminalMultiplexerCoordinator.LeasesChanged -=
                OnTerminalMultiplexerLeasesChanged;
        }
        StopTrackingAgentTerminalSelection(_runtimeWorkspace);
        StopTrackingRecovery(_runtimeWorkspace);
        // Every open workspace, not only the one in front: the others are just
        // as alive, and leaving them behind leaks their sessions.
        Notifications.ForgetAll();
        foreach (var workspace in _openWorkspaces.ToArray())
        {
            workspace.DisposePanels();
        }

        _openWorkspaces.Clear();
        _runtimeSources.Clear();
        _runtimeWorkspace?.DisposePanels();
        _runtimeWorkspace = null;
        WorkspaceEditor = null;
        KeybindingEditorSession = null;
        Onboarding?.Dispose();
        AgentChat?.Cancel();
        AgentChat?.Dispose();
        _runtimeGraphLifetime.Cancel();
        StopRuntimeGraphWatch();
        _historyLifetime.Cancel();
        _runtimeGraphLifetime.Dispose();
        _historyLifetime.Dispose();
    }

    private static string FormatTransferProgress(FilePanelTransferSnapshot snapshot)
    {
        if (snapshot.TotalBytes is > 0 and var total)
        {
            var percent = TransferPercent(snapshot);
            return $"{percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}% · {snapshot.BytesTransferred:N0} / {total:N0} bytes";
        }

        return $"{snapshot.BytesTransferred:N0} bytes";
    }

    private static double TransferPercent(FilePanelTransferSnapshot snapshot) =>
        snapshot.TotalBytes is > 0 and var total
            ? Math.Clamp((double)snapshot.BytesTransferred / total * 100, 0, 100)
            : 0;

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return "Unknown platform";
    }

    private static Symbol WorkspaceIconSymbol(string icon) => WorkspaceIcons.SymbolFor(icon);

    private static string PanelTitle(ScreenPanelKind kind) => kind switch
    {
        ScreenPanelKind.Terminal => "Terminal",
        ScreenPanelKind.Browser => "Browser",
        ScreenPanelKind.FileViewer => "Files",
        ScreenPanelKind.Statistics => "Statistics",
        ScreenPanelKind.ProcessMonitor => "Processes",
        ScreenPanelKind.DatabaseViewer => "Database",
        ScreenPanelKind.Docker => "Docker",
        _ => "Panel",
    };

    private static string PanelTitle(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "Terminal",
        PanelKind.Browser => "Browser",
        PanelKind.FileViewer => "File Viewer",
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        PanelKind.DatabaseViewer => "Database",
        PanelKind.Docker => "Docker",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static PanelKind PanelKindFromDefinition(ScreenPanelKind kind) => kind switch
    {
        ScreenPanelKind.Terminal => PanelKind.Terminal,
        ScreenPanelKind.Browser => PanelKind.Browser,
        ScreenPanelKind.FileViewer => PanelKind.FileViewer,
        ScreenPanelKind.Statistics => PanelKind.Statistics,
        ScreenPanelKind.ProcessMonitor => PanelKind.ProcessMonitor,
        ScreenPanelKind.DatabaseViewer => PanelKind.DatabaseViewer,
        ScreenPanelKind.Docker => PanelKind.Docker,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // Normalized before matching: the label doubles as a visible badge, whose
    // casing follows the interface register, while older recovery rows carry
    // the uppercase form.
    private static PanelKind PanelKindFromRecovery(string? kindLabel) =>
        kindLabel?.Replace(" ", string.Empty).ToUpperInvariant() switch
        {
            "TERMINAL" => PanelKind.Terminal,
            "BROWSER" => PanelKind.Browser,
            "FILES" or "FILEVIEWER" => PanelKind.FileViewer,
            "STATISTICS" => PanelKind.Statistics,
            "PROCESSMONITOR" => PanelKind.ProcessMonitor,
            "DATABASE" or "DATABASEVIEWER" => PanelKind.DatabaseViewer,
            "DOCKER" => PanelKind.Docker,
            _ => throw new InvalidOperationException(
                "The recovered panel kind is not supported by this build."),
        };

    private static string RequireName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ClientId RequireDesktopClientId(
        IAgentApprovalPrincipal approvalPrincipal)
    {
        ArgumentNullException.ThrowIfNull(approvalPrincipal);
        var actor = approvalPrincipal.Actor
            ?? throw new ArgumentException(
                "The agent approval principal must expose a local human actor.",
                nameof(approvalPrincipal));
        if (actor.Kind != ActorKind.Human || actor.ClientId is not { } clientId)
        {
            throw new ArgumentException(
                "The agent approval principal must expose a local human client identity.",
                nameof(approvalPrincipal));
        }

        return clientId;
    }

    private static bool PresentsSameResults(
        IReadOnlyList<LauncherSearchResultViewModel> current,
        IReadOnlyList<LauncherSearchResultViewModel> candidate)
    {
        if (current.Count != candidate.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!current[index].PresentsSameAs(candidate[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces a collection only when the result would look different.
    ///
    /// <see cref="Replace"/> clears and refills, which destroys every realized row
    /// and drops whatever the pointer was over. Most refreshes are provoked by
    /// something unrelated and produce exactly the rows already on screen, so those
    /// have to leave the collection alone.
    /// </summary>
    private static void ReplaceIfChanged<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values,
        Func<T, T, bool> presentsSame)
    {
        if (target.Count == values.Count)
        {
            var unchanged = true;
            for (var index = 0; index < values.Count; index++)
            {
                if (!presentsSame(target[index], values[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        Replace(target, values);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private DefinitionStoreResult<T> Fail<T>(string message)
    {
        SetError(message);
        return DefinitionStoreResult<T>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));
    }

    private void ApplyError(DefinitionStoreError? error) => OperationError = error?.Message;

    private OperationContext NewContext() => OperationContext.ForHuman(ClientId);

    private sealed record RuntimeSessionIdentity(
        SessionId SessionId,
        PanelKind Kind);

}
