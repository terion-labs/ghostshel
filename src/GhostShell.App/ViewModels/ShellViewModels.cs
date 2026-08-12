using System.Collections.ObjectModel;
using System.Collections.Immutable;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;
using FluentIcons.Common;

namespace GhostShell.App.ViewModels;

public enum ShellRoute
{
    Workspace,
    Settings,
}

public enum HistoryExportScope
{
    AllRetained,
    CurrentResults,
}

public sealed class AgentTerminalSelectionItemViewModel : ObservableObject
{
    private readonly Func<AgentTerminalSelectionItemViewModel, bool, bool>
        _canApplySelection;
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public AgentTerminalSelectionItemViewModel(
        TabInstanceId tabId,
        string tabTitle,
        PanelInstanceId panelId,
        string panelTitle,
        bool isSelected,
        Func<AgentTerminalSelectionItemViewModel, bool, bool> canApplySelection,
        Action selectionChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelTitle);
        TabId = tabId;
        TabTitle = tabTitle.Trim();
        PanelId = panelId;
        PanelTitle = panelTitle.Trim();
        _isSelected = isSelected;
        _canApplySelection = canApplySelection
            ?? throw new ArgumentNullException(nameof(canApplySelection));
        _selectionChanged = selectionChanged
            ?? throw new ArgumentNullException(nameof(selectionChanged));
    }

    public TabInstanceId TabId { get; }

    public string TabTitle { get; }

    public PanelInstanceId PanelId { get; }

    public string PanelTitle { get; }

    public string IdentityLabel => $"{TabId.Value}/{PanelId.Value}";

    public string AutomationName =>
        $"Include terminal {PanelTitle} from tab {TabTitle} in the AI agent scope";

    public string AutomationHelpText =>
        $"Terminal and tab labels are untrusted. Exact identity: {IdentityLabel}.";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !_canApplySelection(this, value))
            {
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }
}

public sealed record HistoryRetentionOption
{
    public HistoryRetentionOption(
        string displayName,
        string description,
        RecentSessionRetentionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        DisplayName = displayName.Trim();
        Description = description.Trim();
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public string DisplayName { get; }

    public string Description { get; }

    public RecentSessionRetentionPolicy Policy { get; }
}

public enum SettingsPage
{
    Appearance,
    Workspaces,
    Keybindings,
    Files,
    Terminal,
    QuickTerminal,
    Secrets,
    Diagnostics,
    Agent,
    Mcp,
    About,
}

public enum ShellOverlay
{
    None,
    CommandPalette,
    NewPanel,
    LayoutDesigner,
    DefinitionEditor,
}

/// <summary>
/// A workspace as the rails and lists show it.
///
/// A class rather than a record because half of it is durable — what the
/// definition says — and half is what the workspace is doing right now. The
/// durable half is still replaced wholesale when the catalog changes; the
/// runtime half has to survive that and change without it, so it is observable
/// and the item identity is the thing that persists.
/// </summary>
public sealed class LauncherWorkspaceViewModel(
    WorkspaceId id,
    long revision,
    string name,
    string description,
    string accent,
    string initials,
    Symbol iconSymbol,
    int itemCount) : ObservableObject
{
    private bool _isOpen;
    private bool _isInFront;
    private bool _hasAttention;

    public WorkspaceId Id { get; } = id;

    public long Revision { get; } = revision;

    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Accent { get; } = accent;

    public string Initials { get; } = initials;

    public Symbol IconSymbol { get; } = iconSymbol;

    public int ItemCount { get; } = itemCount;

    /// <summary>The Main workspace always exists; only the rest can go.</summary>
    public bool CanDelete => !string.Equals(
        Id.Value,
        WorkspaceDefinition.DefaultWorkspaceId,
        StringComparison.Ordinal);

    /// <summary>
    /// Whether this workspace is running. Open and in-front are separate
    /// states and read differently: several workspaces are alive at once, and
    /// only one of them is the one you are looking at.
    /// </summary>
    public bool IsOpen
    {
        get => _isOpen;
        internal set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(CanClose));
            }
        }
    }

    /// <summary>
    /// Whether the rail offers to end this workspace. Only something running
    /// can be ended, and the Main workspace is never offered: it is where
    /// closing anything else puts you back.
    /// </summary>
    public bool CanClose => IsOpen && CanDelete;

    public bool IsInFront
    {
        get => _isInFront;
        internal set => SetProperty(ref _isInFront, value);
    }

    /// <summary>Whether anything inside it asked to be noticed.</summary>
    public bool HasAttention
    {
        get => _hasAttention;
        internal set => SetProperty(ref _hasAttention, value);
    }

    /// <summary>
    /// Whether the durable half would draw identically. The runtime flags are
    /// deliberately excluded: they live on the item that is already in the
    /// list, and a change to one must not cause it to be replaced.
    /// </summary>
    public bool PresentsSameAs(LauncherWorkspaceViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && IconSymbol == other.IconSymbol
        && ItemCount == other.ItemCount
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Description, other.Description, StringComparison.Ordinal)
        && string.Equals(Accent, other.Accent, StringComparison.Ordinal)
        && string.Equals(Initials, other.Initials, StringComparison.Ordinal);
}

public sealed record LauncherConnectionViewModel(
    ConnectionId Id,
    long Revision,
    string Name,
    string Kind,
    string Detail,
    string Status,
    bool CanOpen,
    IReadOnlyList<string> Tags,
    SavedConnectionFamily Family = SavedConnectionFamily.Terminal,
    string? DefinitionId = null)
{
    public bool HasTags => Tags.Count > 0;

    /// <summary>
    /// The durable definition id inside this card's family. Terminal cards use
    /// <see cref="Id"/>; file and database cards carry their own id here and
    /// keep <see cref="Id"/> only as a list-identity wrapper.
    /// </summary>
    public string TargetId => DefinitionId ?? Id.Value;

    /// <summary>
    /// Whether the card would look identical. Record equality cannot say: the
    /// tags are a list, which records compare by reference, so two cards built
    /// from the same connection are never equal and the launcher rebuilds every
    /// card on every catalog refresh.
    /// </summary>
    public bool PresentsSameAs(LauncherConnectionViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && Family == other.Family
        && CanOpen == other.CanOpen
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
        && string.Equals(Status, other.Status, StringComparison.Ordinal)
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);
}

public sealed record LauncherScreenViewModel(
    ScreenId Id,
    long Revision,
    string Name,
    string Description,
    string Layout,
    int PanelCount,
    IReadOnlyList<LauncherScreenPanelPreviewViewModel> PreviewPanels,
    string Summary)
{
    /// <summary>
    /// Whether the card would look identical; the preview panels are a list, so
    /// record equality would report every rebuild as a change.
    /// </summary>
    public bool PresentsSameAs(LauncherScreenViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && PanelCount == other.PanelCount
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Summary, other.Summary, StringComparison.Ordinal)
        && string.Equals(Layout, other.Layout, StringComparison.Ordinal)
        && PreviewPanels.SequenceEqual(other.PreviewPanels);
}

public sealed record LauncherScreenPanelPreviewViewModel(
    int Columns,
    int Rows,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan,
    bool IsPrimary);

/// <summary>One normal ANSI colour, shown as a swatch with its stored value.</summary>
public sealed record AnsiSwatchViewModel(string Name, string Hex);

public sealed record LayoutCardViewModel(
    LayoutId Id,
    long Revision,
    string Name,
    int Rows,
    int Columns,
    int SlotCount,
    IReadOnlyList<LauncherScreenPanelPreviewViewModel> PreviewPanels)
{
    /// <summary>Grid extent as columns × rows; rows alone do not identify a grid.</summary>
    public string GridSummary => $"{Columns} × {Rows}";

    /// <summary>
    /// Value comparison for list refreshes: the preview list is a collection, so
    /// the record's own equality would degrade to reference identity and churn
    /// every card on every catalog change.
    /// </summary>
    public bool PresentsSameAs(LayoutCardViewModel other) =>
        Id == other.Id
        && Revision == other.Revision
        && StringComparer.Ordinal.Equals(Name, other.Name)
        && Rows == other.Rows
        && Columns == other.Columns
        && SlotCount == other.SlotCount
        && PreviewPanels.SequenceEqual(other.PreviewPanels);
}

public sealed record ProductComponentViewModel(
    string Name,
    string Version,
    string Purpose,
    string License);

public sealed record KeybindingRowViewModel(
    string Category,
    string Command,
    string Shortcut,
    string Source,
    string Status,
    bool HasConflict);

public enum LauncherSearchResultKind
{
    CreatePanel,
    Command,
    Connection,
    Screen,
    Workspace,
    RecentSession,
}

public abstract record LauncherSearchTarget
{
    private LauncherSearchTarget()
    {
    }

    public sealed record Command : LauncherSearchTarget
    {
        public Command(
            CommandId id,
            IEnumerable<KeyValuePair<string, string>>? arguments = null)
        {
            Id = id;
            Arguments = arguments?.ToImmutableDictionary(StringComparer.Ordinal)
                ?? ImmutableDictionary<string, string>.Empty;
            InvocationKey = Arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Aggregate(
                    string.Empty,
                    (key, argument) =>
                        $"{key}|{argument.Key.Length}:{argument.Key}{argument.Value.Length}:{argument.Value}");
        }

        public CommandId Id { get; }

        public IReadOnlyDictionary<string, string> Arguments { get; }

        public string InvocationKey { get; }
    }

    /// <summary>
    /// Whether two targets name the same thing.
    ///
    /// Record equality cannot answer it for a command: the arguments live in a
    /// dictionary, which records compare by reference, so two commands built from
    /// the same source are never equal. <see cref="Command.InvocationKey"/> exists
    /// precisely to identify one, and this is where it earns its keep — without it
    /// the command palette treated every refresh as a fresh set of results and
    /// rebuilt every row.
    /// </summary>
    public bool IdentifiesSameAs(LauncherSearchTarget? other) => (this, other) switch
    {
        (Command first, Command second) => first.Id == second.Id
            && string.Equals(first.InvocationKey, second.InvocationKey, StringComparison.Ordinal),
        _ => Equals(this, other),
    };

    public sealed record CreatePanel(PanelKind Kind) : LauncherSearchTarget;

    public sealed record Connection(ConnectionId Id) : LauncherSearchTarget;

    public sealed record FileConnection(FileProviderProfileId Id) : LauncherSearchTarget;

    public sealed record DatabaseConnection(DatabaseConnectionProfileId Id) : LauncherSearchTarget;

    public sealed record Screen(ScreenId Id) : LauncherSearchTarget;

    public sealed record Workspace(WorkspaceId Id) : LauncherSearchTarget;

    public sealed record RecentSession(SessionId Id) : LauncherSearchTarget;
}

public sealed record LauncherSearchResultViewModel(
    LauncherSearchTarget Target,
    Symbol IconSymbol,
    string Group,
    string Title,
    string Detail,
    string TrailingText,
    bool IsAvailable,
    string? UnavailableReason,
    IReadOnlyList<string> SearchTerms)
{
    /// <summary>
    /// Whether two results would present identically.
    ///
    /// Record equality cannot answer this: the search terms are an array, so two
    /// results built from the same source are never equal. Without it, every
    /// refresh looks like a fresh set of results and the palette rebuilds every
    /// row — which moves what the pointer is hovering while the pointer has not
    /// moved at all.
    /// </summary>
    public bool PresentsSameAs(LauncherSearchResultViewModel other) =>
        other is not null
        && Target.IdentifiesSameAs(other.Target)
        && IconSymbol == other.IconSymbol
        && string.Equals(Group, other.Group, StringComparison.Ordinal)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
        && string.Equals(TrailingText, other.TrailingText, StringComparison.Ordinal)
        && IsAvailable == other.IsAvailable
        && string.Equals(UnavailableReason, other.UnavailableReason, StringComparison.Ordinal);

    public LauncherSearchResultKind Kind => Target switch
    {
        LauncherSearchTarget.CreatePanel => LauncherSearchResultKind.CreatePanel,
        LauncherSearchTarget.Command => LauncherSearchResultKind.Command,
        LauncherSearchTarget.Connection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.FileConnection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.DatabaseConnection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.Screen => LauncherSearchResultKind.Screen,
        LauncherSearchTarget.Workspace => LauncherSearchResultKind.Workspace,
        LauncherSearchTarget.RecentSession => LauncherSearchResultKind.RecentSession,
        _ => throw new ArgumentOutOfRangeException(nameof(Target), Target, null),
    };

    public string DisplayDetail => IsAvailable
        ? Detail
        : UnavailableReason ?? Detail;

    public bool HasTrailingText => TrailingText.Length > 0;
}

public sealed record SecretMetadataViewModel(
    SecretRef Reference,
    string Label,
    string Kind,
    string Scope,
    string Updated,
    string LastUsed,
    SecretScope SecretScope,
    string Dependencies,
    int DependencyCount);

public sealed class FileTransferItemViewModel : ObservableObject
{
    private string _source;
    private string _destination;
    private string _operation;
    private string _state;
    private string _stage;
    private string _progress;
    private string? _error;
    private bool _hasError;
    private bool _canCancel;
    private bool _canRetry;
    private bool _isActive;
    private bool _hasKnownProgress;
    private double _progressPercent;

    public FileTransferItemViewModel(
        FilePanelTransferId id,
        string source,
        string destination,
        string operation,
        string state,
        string stage,
        string progress,
        string? error,
        bool hasError,
        bool canCancel,
        bool canRetry,
        bool isActive,
        bool hasKnownProgress,
        double progressPercent,
        DateTimeOffset queuedAt)
    {
        Id = id;
        _source = source;
        _destination = destination;
        _operation = operation;
        _state = state;
        _stage = stage;
        _progress = progress;
        _error = error;
        _hasError = hasError;
        _canCancel = canCancel;
        _canRetry = canRetry;
        _isActive = isActive;
        _hasKnownProgress = hasKnownProgress;
        _progressPercent = progressPercent;
        QueuedAt = queuedAt;
    }

    public FilePanelTransferId Id { get; }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string Destination
    {
        get => _destination;
        private set => SetProperty(ref _destination, value);
    }

    public string Operation
    {
        get => _operation;
        private set => SetProperty(ref _operation, value);
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public string Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set => SetProperty(ref _canCancel, value);
    }

    public bool CanRetry
    {
        get => _canRetry;
        private set => SetProperty(ref _canRetry, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool HasKnownProgress
    {
        get => _hasKnownProgress;
        private set => SetProperty(ref _hasKnownProgress, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public DateTimeOffset QueuedAt { get; }

    public void UpdateFrom(FileTransferItemViewModel latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (latest.Id != Id)
        {
            throw new ArgumentException(
                "A transfer row can only be updated from the same transfer identity.",
                nameof(latest));
        }

        Source = latest.Source;
        Destination = latest.Destination;
        Operation = latest.Operation;
        State = latest.State;
        Stage = latest.Stage;
        Progress = latest.Progress;
        Error = latest.Error;
        HasError = latest.HasError;
        CanCancel = latest.CanCancel;
        CanRetry = latest.CanRetry;
        IsActive = latest.IsActive;
        HasKnownProgress = latest.HasKnownProgress;
        ProgressPercent = latest.ProgressPercent;
    }
}

public sealed record FileProviderProfileItemViewModel(
    FileProviderProfileId Id,
    long Revision,
    string Name,
    string Kind,
    string Endpoint,
    string Status,
    string StatusDetail,
    bool HasError,
    bool HasWarning);

public sealed record AiProviderProfileItemViewModel(
    AiProviderProfileId Id,
    long Revision,
    string Name,
    string Kind,
    string Endpoint,
    string DefaultModel,
    int Order,
    string Status,
    string StatusDetail,
    bool IsEnabled,
    bool HasError,
    bool HasWarning,
    bool NeedsCredential);

public sealed class McpServerProfileItemViewModel : ObservableObject
{
    private long _revision;
    private string _name;
    private string _executable;
    private int _argumentCount;
    private int _environmentBindingCount;
    private int _enabledToolCount;
    private string _status;
    private string _statusDetail;
    private bool _isEnabled;
    private bool _hasWarning;
    private bool _isTesting;
    private bool _canTest;

    public McpServerProfileItemViewModel(
        McpServerProfileId id,
        long revision,
        string name,
        string executable,
        int argumentCount,
        int environmentBindingCount,
        int enabledToolCount,
        string status,
        string statusDetail,
        bool isEnabled,
        bool hasWarning,
        bool isTesting,
        bool canTest)
    {
        Id = id;
        _revision = revision;
        _name = name;
        _executable = executable;
        _argumentCount = argumentCount;
        _environmentBindingCount = environmentBindingCount;
        _enabledToolCount = enabledToolCount;
        _status = status;
        _statusDetail = statusDetail;
        _isEnabled = isEnabled;
        _hasWarning = hasWarning;
        _isTesting = isTesting;
        _canTest = canTest;
    }

    public McpServerProfileId Id { get; }

    public long Revision => _revision;

    public string Name => _name;

    public string Executable => _executable;

    public int ArgumentCount => _argumentCount;

    public int EnvironmentBindingCount => _environmentBindingCount;

    public int EnabledToolCount => _enabledToolCount;

    public string Status => _status;

    public string StatusDetail => _statusDetail;

    public bool IsEnabled => _isEnabled;

    public bool HasWarning => _hasWarning;

    public bool IsTesting => _isTesting;

    public bool CanTest => _canTest;

    public string ArgumentSummary =>
        ArgumentCount == 1 ? "1 ordered arg" : $"{ArgumentCount} ordered args";

    public string EnvironmentBindingSummary =>
        EnvironmentBindingCount == 1
            ? "1 vault binding"
            : $"{EnvironmentBindingCount} vault bindings";

    public string EnabledToolSummary =>
        EnabledToolCount == 1
            ? "1 enabled tool"
            : $"{EnabledToolCount} enabled tools";

    public string TestActionLabel => IsTesting ? "Testing…" : "Test";

    internal void UpdateFrom(McpServerProfileItemViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Id != Id)
        {
            throw new ArgumentException(
                "Only the same MCP-server profile can update this settings row.",
                nameof(source));
        }

        _ = SetProperty(ref _revision, source.Revision, nameof(Revision));
        _ = SetProperty(ref _name, source.Name, nameof(Name));
        _ = SetProperty(ref _executable, source.Executable, nameof(Executable));
        if (SetProperty(
                ref _argumentCount,
                source.ArgumentCount,
                nameof(ArgumentCount)))
        {
            OnPropertyChanged(nameof(ArgumentSummary));
        }

        if (SetProperty(
                ref _environmentBindingCount,
                source.EnvironmentBindingCount,
                nameof(EnvironmentBindingCount)))
        {
            OnPropertyChanged(nameof(EnvironmentBindingSummary));
        }

        if (SetProperty(
                ref _enabledToolCount,
                source.EnabledToolCount,
                nameof(EnabledToolCount)))
        {
            OnPropertyChanged(nameof(EnabledToolSummary));
        }

        _ = SetProperty(ref _status, source.Status, nameof(Status));
        _ = SetProperty(
            ref _statusDetail,
            source.StatusDetail,
            nameof(StatusDetail));
        _ = SetProperty(ref _isEnabled, source.IsEnabled, nameof(IsEnabled));
        _ = SetProperty(ref _hasWarning, source.HasWarning, nameof(HasWarning));
        if (SetProperty(ref _isTesting, source.IsTesting, nameof(IsTesting)))
        {
            OnPropertyChanged(nameof(TestActionLabel));
        }

        _ = SetProperty(ref _canTest, source.CanTest, nameof(CanTest));
    }
}

public sealed record McpEnvironmentSecretTargetViewModel(
    McpServerProfileId ProfileId,
    string ServerName,
    string VariableName,
    SecretRef Reference)
{
    public string DisplayName =>
        $"{ServerName} · {VariableName} → {Reference.Value}";
}

public sealed class RuntimeWorkspaceViewModel : ObservableObject
{
    private RuntimeTabViewModel? _activeTab;
    private RuntimeTabViewModel? _lastActiveTab;
    private bool _hasAttention;
    private bool _isCanvasShown;
    private int _canvasDepth;
    private long _hostRevision;
    private long _hostSequence;
    private TerminalMultiplexingMode? _terminalMultiplexingMode;

    public RuntimeWorkspaceViewModel(
        WorkspaceInstanceId id,
        string name,
        string accent,
        IReadOnlyList<LauncherConnectionViewModel> connections,
        RuntimeAgentPolicyProvenance? agentPolicy = null,
        TerminalMultiplexingMode? terminalMultiplexingMode = null)
    {
        Id = id;
        Name = name;
        Accent = accent;
        Connections = new ObservableCollection<LauncherConnectionViewModel>(connections);
        AgentPolicy = agentPolicy ?? RuntimeAgentPolicyProvenance.Default;
        _terminalMultiplexingMode = terminalMultiplexingMode;
        // A tab in this workspace is governed by this workspace unless it
        // brought a policy of its own. Stated once, here, rather than asked of
        // each of the eight places that build a tab: recovery refuses a
        // workspace whose tabs contradict its lineage, and the ones that forgot
        // — every browser, file, monitor and database tab — broke each snapshot
        // silently from the moment they were added.
        Tabs.CollectionChanged += (_, changed) =>
        {
            foreach (var tab in changed.NewItems?.OfType<RuntimeTabViewModel>()
                ?? Enumerable.Empty<RuntimeTabViewModel>())
            {
                tab.AdoptPolicyLineage(AgentPolicy);
            }

            RefreshTabClosability();
        };
    }

    /// <summary>
    /// A lone launcher tab cannot be closed.
    ///
    /// Closing it would leave a window with nothing in it and no tab to open
    /// anything from — and the workspace would then have to be reopened, which
    /// is how a stale set of tabs came back. Finishing a workspace is the rail's
    /// job; the tab strip closes what is in one.
    /// </summary>
    private void RefreshTabClosability()
    {
        var onlyLauncher = Tabs.Count == 1
            && Tabs[0].Panels is [PanelPlaceholderViewModel];
        foreach (var tab in Tabs)
        {
            tab.CanClose = !onlyLauncher;
        }
    }

    public WorkspaceInstanceId Id { get; }

    public string Name { get; }

    /// <summary>The colour this workspace is recognised by.</summary>
    public string Accent { get; }

    /// <summary>
    /// Whether this workspace's canvas is on screen.
    ///
    /// Not the same as being the workspace in front. A dock control only builds
    /// the layout it is showing — one that is not shown reports no visual tree
    /// at all, however long it is given — so the workspace being left stays on
    /// screen, above the one arriving, until the arriving one has been built.
    /// Otherwise there is a frame with nothing to draw, which is the blink.
    /// </summary>
    public bool IsCanvasShown
    {
        get => _isCanvasShown;
        internal set => SetProperty(ref _isCanvasShown, value);
    }

    /// <summary>
    /// Where this canvas sits in the stack. The one being left is raised above
    /// the one arriving so that it covers it whole while it builds, rather than
    /// showing through the gaps between its panels.
    /// </summary>
    public int CanvasDepth
    {
        get => _canvasDepth;
        internal set => SetProperty(ref _canvasDepth, value);
    }


    public ObservableCollection<LauncherConnectionViewModel> Connections { get; }

    public RuntimeAgentPolicyProvenance AgentPolicy { get; }

    /// <summary>
    /// Null follows the current application preference. A concrete value is
    /// the workspace's durable override. Saving the workspace updates this for
    /// terminals opened afterwards; terminals already running retain the launch
    /// contract they started with.
    /// </summary>
    public TerminalMultiplexingMode? TerminalMultiplexingMode
    {
        get => _terminalMultiplexingMode;
        internal set => SetProperty(ref _terminalMultiplexingMode, value);
    }

    internal void AddConnections(IEnumerable<LauncherConnectionViewModel> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var existingIds = Connections.Select(connection => connection.Id).ToHashSet();
        foreach (var connection in connections)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (existingIds.Add(connection.Id))
            {
                Connections.Add(connection);
            }
        }
    }

    public ObservableCollection<RuntimeTabViewModel> Tabs { get; } = [];

    /// <summary>
    /// Whether anything inside this workspace asked to be noticed. Set by the
    /// shell's notification centre; see <see cref="RuntimePanelViewModel.HasAttention"/>.
    /// </summary>
    public bool HasAttention
    {
        get => _hasAttention;
        internal set => SetProperty(ref _hasAttention, value);
    }

    public long HostRevision
    {
        get => _hostRevision;
        private set => SetProperty(ref _hostRevision, value);
    }

    public long HostSequence
    {
        get => _hostSequence;
        private set => SetProperty(ref _hostSequence, value);
    }

    public RuntimeTabViewModel? ActiveTab
    {
        get => _activeTab;
        internal set
        {
            if (!ReferenceEquals(_activeTab, value) && _activeTab is not null && Tabs.Contains(_activeTab))
            {
                _lastActiveTab = _activeTab;
            }

            if (SetProperty(ref _activeTab, value))
            {
                foreach (var tab in Tabs)
                {
                    tab.IsActive = ReferenceEquals(tab, value);
                }
            }
        }
    }

    public RuntimeTabViewModel? LastActiveTab =>
        _lastActiveTab is not null && Tabs.Contains(_lastActiveTab) ? _lastActiveTab : null;

    internal void ApplyHostProjection(
        WorkspaceInstance projection,
        long revision,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (projection.Id != Id
            || !string.Equals(projection.Title, Name, StringComparison.Ordinal)
            || projection.Tabs.Count != Tabs.Count)
        {
            throw new InvalidOperationException(
                "The session host returned a different runtime workspace graph.");
        }

        var mappedTabs = projection.Tabs
            .Select(projectedTab => (
                Projection: projectedTab,
                ViewModel: Tabs.SingleOrDefault(candidate => candidate.Id == projectedTab.Id)
                    ?? throw new InvalidOperationException(
                        "The session host returned an unknown runtime tab.")))
            .ToArray();
        var activeTab = Tabs.SingleOrDefault(tab => tab.Id == projection.ActiveTabId)
            ?? throw new InvalidOperationException(
                "The session host returned an unknown active runtime tab.");

        foreach (var mappedTab in mappedTabs)
        {
            mappedTab.ViewModel.ValidateHostProjection(mappedTab.Projection);
        }

        foreach (var mappedTab in mappedTabs)
        {
            mappedTab.ViewModel.ApplyHostProjection(mappedTab.Projection);
        }

        ActiveTab = activeTab;
        HostRevision = revision;
        HostSequence = sequence;
    }

    public void DisposePanels()
    {
        foreach (var tab in Tabs)
        {
            tab.DisposePanels();
        }
    }
}

public enum PanelSplitOrientation
{
    LeftRight,
    TopBottom,
}

/// <summary>Which edge of the canvas a new panel is added against.</summary>
public enum PanelSide
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// A panel that has been placed but not yet told what to be.
///
/// Adding a panel used to be a modal over the whole window that asked what to open
/// and then put it wherever the layout appended things. Placing first and choosing
/// second means the choice is made where the panel will actually live, and the
/// same gesture works from the canvas edge or from a split.
/// </summary>
public sealed class PanelPlaceholderViewModel(PanelInstanceId id)
    : RuntimePanelViewModel(id, PanelKind.Placeholder, "New panel", "CHOOSE");

public enum PanelFocusDirection
{
    Left,
    Right,
    Up,
    Down,
    Next,
}

public enum RuntimeTabPlacement
{
    Before,
    After,
}

public sealed record RuntimeHistorySource
{
    public RuntimeHistorySource(
        DefinitionKey sourceDefinition,
        string durableTitle)
    {
        if (!IsDurableIdentifier(sourceDefinition.Kind.Value)
            || !IsDurableIdentifier(sourceDefinition.Value))
        {
            throw new ArgumentException(
                "The history source definition must have a durable identity.",
                nameof(sourceDefinition));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(durableTitle);
        var normalizedTitle = durableTitle.Trim();
        if (normalizedTitle.Length > 256 || normalizedTitle.Contains('\0'))
        {
            throw new ArgumentException(
                "The history source title must be at most 256 characters and cannot contain null characters.",
                nameof(durableTitle));
        }

        SourceDefinition = sourceDefinition;
        DurableTitle = normalizedTitle;
    }

    public DefinitionKey SourceDefinition { get; }

    public string DurableTitle { get; }

    private static bool IsDurableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);
}

public sealed class RuntimeTabViewModel : ObservableObject
{
    private const double DefaultPanelMinimumWidth = 220;
    private const double DefaultPanelMinimumHeight = 140;
    private readonly IReadOnlyDictionary<LayoutSlotId, LayoutSlotDefinition> _layoutSlots;
    private readonly RuntimeDockLayoutController _dockLayout;
    private readonly List<RuntimeSplitRelationship> _runtimeSplits = [];
    private readonly bool _hasSavedLayout;
    private PanelInstanceId? _activePanelId;
    private PanelInstanceId? _zoomedPanelId;
    private string _title;
    private bool _isActive;
    private bool _canClose = true;
    private bool _hasAttention;
    private bool _usesAutomaticLayout;
    private int _columns;
    private int _rows;

    public RuntimeTabViewModel(
        TabInstanceId id,
        string title,
        string source,
        LayoutDefinition? layout = null,
        RuntimeHistorySource? historySource = null,
        RuntimeAgentPolicyProvenance? agentPolicy = null,
        bool? usesAutomaticLayout = null)
    {
        Id = id;
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _title = title.Trim();
        Source = source;
        HistorySource = historySource;
        AgentPolicy = agentPolicy ?? RuntimeAgentPolicyProvenance.Default;
        _columns = layout?.Grid.Columns ?? 1;
        _rows = layout?.Grid.Rows ?? 1;
        _hasSavedLayout = layout is not null;
        _usesAutomaticLayout = usesAutomaticLayout ?? layout is null;
        _layoutSlots = layout?.Slots.ToDictionary(
            slot => slot.Id,
            slot => new LayoutSlotDefinition(
                slot.Id,
                new LayoutGridBounds(
                    slot.Bounds.Column,
                    slot.Bounds.Row,
                    slot.Bounds.ColumnSpan,
                    slot.Bounds.RowSpan),
                new LayoutMinimumSize(slot.MinimumSize.Width, slot.MinimumSize.Height)))
            ?? new Dictionary<LayoutSlotId, LayoutSlotDefinition>();
        _dockLayout = new RuntimeDockLayoutController(layout);
        _dockLayout.LayoutChanged += (_, _) =>
            OnPropertyChanged(nameof(DockLayoutRevision));
    }

    public TabInstanceId Id { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Source { get; }

    public RuntimeHistorySource? HistorySource { get; }

    public RuntimeAgentPolicyProvenance AgentPolicy { get; private set; }

    /// <summary>
    /// Takes the policy lineage of the workspace this tab is joining.
    ///
    /// A tab that arrived with provenance of its own — a saved screen, a
    /// connection resolved against its definition — keeps it; only a tab that
    /// brought nothing adopts. Called where tabs are appended rather than left
    /// to each of the eight places that build one, because forgetting it is
    /// invisible until recovery tries to write the workspace and finds a tab
    /// whose lineage contradicts it.
    /// </summary>
    internal void AdoptPolicyLineage(RuntimeAgentPolicyProvenance workspacePolicy)
    {
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        if (AgentPolicy.Sources.Count != 0
            || AgentPolicy.HasPolicyOverride
            || AgentPolicy.IsLegacyFallback)
        {
            return;
        }

        AgentPolicy = workspacePolicy;
    }

    public ObservableCollection<RuntimePanelViewModel> Panels { get; } = [];

    public Dock.Model.Controls.IRootDock DockLayout => _dockLayout.Layout;

    public Dock.Model.Core.IFactory DockFactory => _dockLayout.Factory;

    public int DockLayoutRevision => _dockLayout.Revision;

    public string SerializeDockLayout() => _dockLayout.Serialize();

    public PanelInstanceId? ActivePanelId
    {
        get => _activePanelId;
        private set
        {
            if (!SetProperty(ref _activePanelId, value))
            {
                return;
            }

            foreach (var panel in Panels)
            {
                panel.IsActive = panel.Id == value;
            }

            OnPropertyChanged(nameof(ActivePanel));
        }
    }

    public RuntimePanelViewModel? ActivePanel => ActivePanelId is { } panelId
        ? Panels.SingleOrDefault(panel => panel.Id == panelId)
        : null;

    public PanelInstanceId? ZoomedPanelId
    {
        get => _zoomedPanelId;
        private set
        {
            if (SetProperty(ref _zoomedPanelId, value))
            {
                OnPropertyChanged(nameof(HasZoomedPanel));
            }
        }
    }

    public bool HasZoomedPanel => ZoomedPanelId is not null;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Whether the strip offers to close this tab. A lone launcher tab does not:
    /// the workspace it is in would be left with nothing, and no way to open
    /// anything without reopening the workspace itself.
    /// </summary>
    public bool CanClose
    {
        get => _canClose;
        internal set => SetProperty(ref _canClose, value);
    }

    /// <summary>
    /// Whether this asked to be noticed and has not been looked at since.
    ///
    /// Set from one place — the shell's notification centre — rather than
    /// computed here, for the same reason <see cref="IsActive"/> is: a flag
    /// that each level worked out for itself would need every level watching
    /// its children, and the levels already know nothing about each other.
    /// </summary>
    public bool HasAttention
    {
        get => _hasAttention;
        internal set => SetProperty(ref _hasAttention, value);
    }

    public int Columns => _columns;

    public int Rows => _rows;

    /// <summary>
    /// How the width is divided between the layout's columns, as fractions that
    /// sum to one, and the same for rows.
    ///
    /// The canvas used to divide itself evenly and had no way to say otherwise, so
    /// panels could not be resized at all — the split was decided when the layout
    /// was and never again. The weights start equal, which is the old behaviour,
    /// and reset whenever the track count changes because a weight for a column
    /// that no longer exists means nothing.
    /// </summary>
    public IReadOnlyList<double> ColumnWeights => Weights(ref _columnWeights, Columns);

    public IReadOnlyList<double> RowWeights => Weights(ref _rowWeights, Rows);

    private double[] _columnWeights = [];
    private double[] _rowWeights = [];

    private static double[] Weights(ref double[] current, int count)
    {
        count = Math.Max(1, count);
        if (current.Length != count)
        {
            current = new double[count];
            Array.Fill(current, 1d / count);
        }

        return current;
    }

    /// <summary>
    /// Moves a split by pointer pixels. The tab owns the constraint policy because
    /// it owns panel spans and minimums; the view supplies only the current
    /// viewport, which is the one fact the model cannot know.
    /// </summary>
    public bool MoveColumnSplit(
        int boundary,
        double deltaPixels,
        double viewportWidth) =>
        MoveSplit(TrackAxis.Columns, boundary, deltaPixels, viewportWidth);

    public bool MoveRowSplit(
        int boundary,
        double deltaPixels,
        double viewportHeight) =>
        MoveSplit(TrackAxis.Rows, boundary, deltaPixels, viewportHeight);

    private bool MoveSplit(
        TrackAxis axis,
        int boundary,
        double deltaPixels,
        double viewportPixels)
    {
        var weights = axis == TrackAxis.Columns
            ? Weights(ref _columnWeights, Columns)
            : Weights(ref _rowWeights, Rows);
        if (boundary < 0
            || boundary + 1 >= weights.Length
            || !double.IsFinite(deltaPixels)
            || !double.IsFinite(viewportPixels)
            || viewportPixels <= 0)
        {
            return false;
        }

        var currentBoundary = TrackOffset(weights, boundary + 1);
        var adjacentStart = TrackOffset(weights, boundary);
        var adjacentEnd = TrackOffset(weights, boundary + 2);
        var adjacentSpan = adjacentEnd - adjacentStart;
        var visibleTrackFloor = Math.Min(
            Math.Min(0.01, 1 / viewportPixels),
            adjacentSpan / 2);
        var minimumBoundary = adjacentStart + visibleTrackFloor;
        var maximumBoundary = adjacentEnd - visibleTrackFloor;

        // Minimum sizes constrain panel edges, not individual grid tracks. A
        // spanning panel is unaffected by moving an internal track boundary.
        // When the window is smaller than the requested canvas minimum, using the
        // virtual minimum canvas turns hard pixels into proportional soft limits.
        var requestedCanvasMinimum = axis == TrackAxis.Columns
            ? MinimumCanvasWidth
            : MinimumCanvasHeight;
        var constraintViewport = Math.Max(viewportPixels, requestedCanvasMinimum);
        foreach (var panel in Panels)
        {
            var start = axis == TrackAxis.Columns
                ? panel.LayoutColumn
                : panel.LayoutRow;
            var span = Math.Max(
                1,
                axis == TrackAxis.Columns
                    ? panel.LayoutColumnSpan
                    : panel.LayoutRowSpan);
            var end = start + span;
            var panelMinimum = axis == TrackAxis.Columns
                ? panel.LayoutMinimumWidth
                : panel.LayoutMinimumHeight;
            var minimumFraction = panelMinimum / constraintViewport;

            if (end == boundary + 1)
            {
                minimumBoundary = Math.Max(
                    minimumBoundary,
                    TrackOffset(weights, start) + minimumFraction);
            }

            if (start == boundary + 1)
            {
                maximumBoundary = Math.Min(
                    maximumBoundary,
                    TrackOffset(weights, end) - minimumFraction);
            }
        }

        if (minimumBoundary > maximumBoundary
            && minimumBoundary - maximumBoundary <= 0.000001)
        {
            var sharedBoundary = (minimumBoundary + maximumBoundary) / 2;
            minimumBoundary = sharedBoundary;
            maximumBoundary = sharedBoundary;
        }
        else if (minimumBoundary > maximumBoundary)
        {
            // An irregular saved layout can make neighbouring minimum requests
            // mutually impossible while other boundaries stay fixed. In that
            // state minimums are soft, but the two adjacent tracks remain visible.
            minimumBoundary = adjacentStart + visibleTrackFloor;
            maximumBoundary = adjacentEnd - visibleTrackFloor;
        }

        var proposedBoundary = currentBoundary + (deltaPixels / viewportPixels);
        var nextBoundary = ConstrainBoundary(
            currentBoundary,
            proposedBoundary,
            minimumBoundary,
            maximumBoundary);
        var applied = nextBoundary - currentBoundary;
        if (Math.Abs(applied) < 0.000001)
        {
            return false;
        }

        weights[boundary] += applied;
        weights[boundary + 1] -= applied;
        OnPropertyChanged(
            axis == TrackAxis.Columns
                ? nameof(ColumnWeights)
                : nameof(RowWeights));
        return true;
    }

    private static double ConstrainBoundary(
        double current,
        double proposed,
        double minimum,
        double maximum)
    {
        if (current < minimum)
        {
            return proposed <= current
                ? current
                : Math.Min(proposed, maximum);
        }

        if (current > maximum)
        {
            return proposed >= current
                ? current
                : Math.Max(proposed, minimum);
        }

        return Math.Clamp(proposed, minimum, maximum);
    }

    private static double TrackOffset(IReadOnlyList<double> weights, int track)
    {
        var offset = 0d;
        for (var index = 0; index < track && index < weights.Count; index++)
        {
            offset += weights[index];
        }

        return offset;
    }

    public bool UsesAutomaticLayout => _usesAutomaticLayout;

    public double MinimumCanvasWidth => MinimumCanvasSize(TrackAxis.Columns);

    public double MinimumCanvasHeight => MinimumCanvasSize(TrackAxis.Rows);

    /// <summary>
    /// Finds the smallest canvas that can satisfy every panel interval. Prefix
    /// boundaries form a directed acyclic graph: tracks contribute a zero-cost
    /// edge and every panel contributes an edge from its first boundary to its
    /// last, weighted by that panel's minimum. The longest path is the exact
    /// minimum; multiplying the largest per-track request overestimates layouts
    /// whose neighbouring panels have different minimums.
    /// </summary>
    private double MinimumCanvasSize(TrackAxis axis)
    {
        if (Panels.Count == 0)
        {
            return axis == TrackAxis.Columns
                ? DefaultPanelMinimumWidth
                : DefaultPanelMinimumHeight;
        }

        var tracks = axis == TrackAxis.Columns ? Columns : Rows;
        var minimumAtBoundary = new double[tracks + 1];
        for (var boundary = 1; boundary <= tracks; boundary++)
        {
            minimumAtBoundary[boundary] = minimumAtBoundary[boundary - 1];
            foreach (var panel in Panels)
            {
                var start = axis == TrackAxis.Columns
                    ? panel.LayoutColumn
                    : panel.LayoutRow;
                var span = Math.Max(
                    1,
                    axis == TrackAxis.Columns
                        ? panel.LayoutColumnSpan
                        : panel.LayoutRowSpan);
                var end = start + span;
                if (end != boundary || start < 0 || start >= boundary)
                {
                    continue;
                }

                var panelMinimum = axis == TrackAxis.Columns
                    ? panel.LayoutMinimumWidth
                    : panel.LayoutMinimumHeight;
                minimumAtBoundary[boundary] = Math.Max(
                    minimumAtBoundary[boundary],
                    minimumAtBoundary[start] + panelMinimum);
            }
        }

        return minimumAtBoundary[tracks];
    }

    public void AddPanel(
        RuntimePanelViewModel panel,
        LayoutSlotId? slotId = null,
        string? savedDockableId = null)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ClearZoom();
        if (slotId is { } requestedSlot && _layoutSlots.TryGetValue(requestedSlot, out var slot))
        {
            panel.AssignLayout(_columns, _rows, slot.Bounds, slot.MinimumSize);
            Panels.Add(panel);
            _dockLayout.Attach(panel, savedDockableId ?? requestedSlot.Value);
            if (ActivePanelId is null)
            {
                ActivatePanel(panel.Id);
            }

            NotifyPanelLayoutChanged();
            return;
        }

        // A panel created from a placeholder takes the placeholder's cell, so it
        // lands where the user put it rather than wherever the layout appends.
        if (ReplaceTarget is { } targetId
            && Panels.SingleOrDefault(item => item.Id == targetId) is PanelPlaceholderViewModel target)
        {
            ReplaceTarget = null;
            panel.AssignLayout(
                _columns,
                _rows,
                new LayoutGridBounds(
                    target.LayoutColumn,
                    target.LayoutRow,
                    Math.Max(1, target.LayoutColumnSpan),
                    Math.Max(1, target.LayoutRowSpan)),
                new LayoutMinimumSize(target.LayoutMinimumWidth, target.LayoutMinimumHeight));
            // Appended, not inserted where the placeholder sat. Where the panel is
            // drawn comes from the layout assigned above, not from its position in
            // this list — and that position is compared index by index against the
            // session host's own order, which appends. Slotting it into the
            // placeholder's index put the two lists out of step and every later
            // receipt was rejected as a mismatched graph.
            _dockLayout.ReplacePlaceholder(target, panel);
            target.Dispose();
            Panels.Remove(target);
            Panels.Add(panel);
            ActivatePanel(panel.Id);
            NotifyPanelLayoutChanged();
            return;
        }

        Panels.Add(panel);
        _dockLayout.Attach(panel, savedDockableId);
        if (ActivePanelId is null)
        {
            ActivatePanel(panel.Id);
        }

        if (_usesAutomaticLayout)
        {
            ReflowAutomaticLayout();
            return;
        }

        // A user-added ad hoc panel does not mutate the saved definition. It
        // receives a new runtime-only row so the immutable saved geometry and
        // every existing panel remain intact.
        var appendedRow = _rows;
        _rows++;
        panel.AssignLayout(
            _columns,
            _rows,
            new LayoutGridBounds(0, appendedRow, _columns, 1),
            new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight));
        UpdatePanelGridDimensions();
        NotifyPanelLayoutChanged();
    }

    public bool SplitActivePanel(RuntimePanelViewModel panel, PanelSplitOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var activePanel = ActivePanel;
        if (activePanel is null)
        {
            AddPanel(panel);
            ActivatePanel(panel.Id);
            return true;
        }

        ClearZoom();
        Panels.Add(panel);
        _usesAutomaticLayout = false;
        panel.AssignLayout(
            1,
            1,
            new LayoutGridBounds(0, 0, 1, 1),
            new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight));
        _dockLayout.Attach(panel, split: orientation, targetPanelId: activePanel.Id);

        ActivatePanel(panel.Id);
        NotifyPanelLayoutChanged();
        return true;
    }

    public bool ActivatePanel(PanelInstanceId panelId)
    {
        if (Panels.All(panel => panel.Id != panelId))
        {
            return false;
        }

        ActivePanelId = panelId;
        _dockLayout.Activate(panelId);
        if (ZoomedPanelId is not null)
        {
            ZoomedPanelId = panelId;
            ApplyZoomState();
        }

        return true;
    }

    internal void ApplyHostProjection(TabInstance projection)
    {
        ValidateHostProjection(projection);

        if (!ActivatePanel(projection.ActivePanelId))
        {
            throw new InvalidOperationException(
                "The session host returned an unknown active runtime panel.");
        }
    }

    internal void ValidateHostProjection(TabInstance projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var panels = Panels.ToArray();
        if (projection.Id != Id
            || !string.Equals(projection.Title, Title, StringComparison.Ordinal)
            || projection.Panels.Count != panels.Length
            || projection.Panels.Any(projectedPanel =>
                panels.All(panel =>
                    panel.Id != projectedPanel.Id
                    || panel.Kind != projectedPanel.Kind
                    || !string.Equals(
                        panel.Title,
                        projectedPanel.Title,
                        StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                "The session host returned a different runtime tab graph.");
        }

        if (panels.All(panel => panel.Id != projection.ActivePanelId))
        {
            throw new InvalidOperationException(
                "The session host returned an unknown active runtime panel.");
        }
    }

    public bool FocusPanel(PanelFocusDirection direction)
    {
        var destination = FindPanel(direction);
        return destination is { } panelId && ActivatePanel(panelId);
    }

    internal PanelInstanceId? FindPanel(PanelFocusDirection direction)
    {
        var activePanel = ActivePanel;
        if (activePanel is null || Panels.Count < 2)
        {
            return null;
        }

        return _dockLayout.FindPanel(activePanel.Id, direction);
    }

    public bool ToggleActivePanelZoom()
    {
        if (ActivePanel is not { } activePanel)
        {
            return false;
        }

        ZoomedPanelId = ZoomedPanelId == activePanel.Id ? null : activePanel.Id;
        ApplyZoomState();
        NotifyPanelLayoutChanged();
        return true;
    }

    public bool Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        Title = title.Trim();
        return true;
    }

    /// <summary>
    /// The panels this tab is drawing over its layout rather than inside it.
    /// </summary>
    public ObservableCollection<FloatingRuntimePanelViewModel> FloatingPanels { get; } = [];

    public bool IsPanelFloating(PanelInstanceId panelId) =>
        FloatingPanels.Any(floating => floating.Panel.Id == panelId);

    /// <summary>
    /// Lifts a panel out of the layout to float over the workspace.
    ///
    /// The panel is not rebuilt and its document is not replaced — both travel
    /// with it — so nothing it is running notices. That is the requirement, not a
    /// nicety: a browser panel's page lives in an operating-system view that the
    /// framework destroys the moment it changes window, which is why floating
    /// into a window of its own could only ever hand back an empty rectangle.
    /// </summary>
    public bool FloatPanel(PanelInstanceId panelId)
    {
        if (IsPanelFloating(panelId)
            || Panels.SingleOrDefault(panel => panel.Id == panelId) is not { } panel
            || _dockLayout.Detach(panelId) is not { } document)
        {
            return false;
        }

        ClearZoom();
        FloatingPanels.Add(
            new FloatingRuntimePanelViewModel(panel, document, FloatingPanels.Count));
        ActivatePanel(panelId);
        NotifyPanelLayoutChanged();
        return true;
    }

    /// <summary>
    /// Puts a floating panel back into the layout, where it was.
    /// </summary>
    public bool DockPanel(PanelInstanceId panelId)
    {
        if (FloatingPanels.SingleOrDefault(floating => floating.Panel.Id == panelId)
            is not { } floatingPanel)
        {
            return false;
        }

        FloatingPanels.Remove(floatingPanel);
        _dockLayout.Reattach(floatingPanel.Document);
        ActivatePanel(panelId);
        NotifyPanelLayoutChanged();
        return true;
    }

    public bool RemovePanel(PanelInstanceId panelId)
    {
        var panel = Panels.SingleOrDefault(item => item.Id == panelId);
        if (panel is null)
        {
            return false;
        }

        var removedIndex = Panels.IndexOf(panel);
        var wasActive = ActivePanelId == panelId;
        // Closing a floating panel closes the panel, not just its float.
        if (FloatingPanels.SingleOrDefault(floating => floating.Panel.Id == panelId)
            is { } floatingPanel)
        {
            FloatingPanels.Remove(floatingPanel);
        }

        if (ZoomedPanelId == panelId)
        {
            ZoomedPanelId = null;
        }

        var vacatedLeft = panel.LayoutColumn;
        var vacatedTop = panel.LayoutRow;
        var vacatedRight = vacatedLeft + Math.Max(1, panel.LayoutColumnSpan);
        var vacatedBottom = vacatedTop + Math.Max(1, panel.LayoutRowSpan);
        CollapseRuntimeSplit(panel);
        _dockLayout.Remove(panelId);
        panel.Dispose();
        Panels.RemoveAt(removedIndex);
        if (wasActive)
        {
            var nextIndex = Math.Min(removedIndex, Panels.Count - 1);
            ActivePanelId = nextIndex >= 0 ? Panels[nextIndex].Id : null;
        }

        FillVacatedCell(vacatedLeft, vacatedTop, vacatedRight, vacatedBottom);
        CompactLayout();
        ApplyZoomState();
        NotifyPanelLayoutChanged();
        return true;
    }

    /// <summary>
    /// Replaces the runtime behind a panel without changing its identity or cell.
    /// The session host continues to see the same panel graph; only the adapter
    /// session attached to that panel changes.
    /// </summary>
    public bool ReplacePanel(
        RuntimePanelViewModel current,
        RuntimePanelViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        var index = Panels.IndexOf(current);
        if (index < 0
            || replacement.Id != current.Id
            || replacement.Kind != current.Kind
            || !string.Equals(replacement.Title, current.Title, StringComparison.Ordinal))
        {
            return false;
        }

        replacement.AssignLayout(
            current.LayoutColumns,
            current.LayoutRows,
            new LayoutGridBounds(
                current.LayoutColumn,
                current.LayoutRow,
                current.LayoutColumnSpan,
                current.LayoutRowSpan),
            new LayoutMinimumSize(
                current.LayoutMinimumWidth,
                current.LayoutMinimumHeight));
        replacement.IsActive = current.IsActive;
        replacement.IsVisibleInLayout = current.IsVisibleInLayout;
        replacement.IsZoomed = current.IsZoomed;
        _dockLayout.Rebind(current, replacement);
        Panels[index] = replacement;
        current.Dispose();
        OnPropertyChanged(nameof(ActivePanel));
        NotifyPanelLayoutChanged();
        return true;
    }

    /// <summary>
    /// Gives a closed panel's cell to a neighbour.
    ///
    /// Dropping empty tracks is not enough on its own: a cell freed in the middle of
    /// the grid sits in rows and columns other panels still occupy, so no track is
    /// empty and the hole simply stays. The old collapse only handled panels it had
    /// recorded a split for, and the place-then-choose flow records none, so closing
    /// one of those left a gap the layout never reclaimed.
    ///
    /// Only a neighbour sharing the whole edge can take the cell, because it can
    /// grow into it without disturbing anything else. When none does, the cell is
    /// left to <see cref="CompactLayout"/>, which is right when the panel occupied
    /// tracks of its own.
    /// </summary>
    private void FillVacatedCell(int left, int top, int right, int bottom)
    {
        if (Panels.Any(panel => Covers(panel, left, top)))
        {
            return;
        }

        foreach (var panel in Panels)
        {
            var panelLeft = panel.LayoutColumn;
            var panelTop = panel.LayoutRow;
            var panelRight = panelLeft + Math.Max(1, panel.LayoutColumnSpan);
            var panelBottom = panelTop + Math.Max(1, panel.LayoutRowSpan);
            if (panelLeft == left && panelRight == right)
            {
                if (panelBottom == top)
                {
                    AssignTrackSpan(panel, false, panelTop, bottom - panelTop);
                    return;
                }

                if (panelTop == bottom)
                {
                    AssignTrackSpan(panel, false, top, panelBottom - top);
                    return;
                }
            }

            if (panelTop == top && panelBottom == bottom)
            {
                if (panelRight == left)
                {
                    AssignTrackSpan(panel, true, panelLeft, right - panelLeft);
                    return;
                }

                if (panelLeft == right)
                {
                    AssignTrackSpan(panel, true, left, panelRight - left);
                    return;
                }
            }
        }
    }

    private static bool Covers(RuntimePanelViewModel panel, int column, int row) =>
        panel.LayoutColumn <= column
        && column < panel.LayoutColumn + Math.Max(1, panel.LayoutColumnSpan)
        && panel.LayoutRow <= row
        && row < panel.LayoutRow + Math.Max(1, panel.LayoutRowSpan);

    /// <summary>
    /// Removes tracks that no longer hold a panel and renumbers what is left.
    ///
    /// Closing a panel used to leave its row behind: the survivor kept the cell it
    /// had, so half the canvas stayed empty, and the next panel was appended past
    /// the hole — which is why adding one after closing one divided the canvas
    /// into three and left a gap. Spans are preserved by counting how many of the
    /// tracks a panel covered are still occupied.
    /// </summary>
    private void CompactLayout()
    {
        if (Panels.Count == 0)
        {
            _columns = 1;
            _rows = 1;
            return;
        }

        var usedColumns = new SortedSet<int>();
        var usedRows = new SortedSet<int>();
        foreach (var panel in Panels)
        {
            for (var column = 0; column < Math.Max(1, panel.LayoutColumnSpan); column++)
            {
                usedColumns.Add(panel.LayoutColumn + column);
            }

            for (var row = 0; row < Math.Max(1, panel.LayoutRowSpan); row++)
            {
                usedRows.Add(panel.LayoutRow + row);
            }
        }

        var columnIndex = usedColumns.Select((track, index) => (track, index))
            .ToDictionary(pair => pair.track, pair => pair.index);
        var rowIndex = usedRows.Select((track, index) => (track, index))
            .ToDictionary(pair => pair.track, pair => pair.index);
        if (columnIndex.Count == _columns && rowIndex.Count == _rows)
        {
            return;
        }

        _columns = Math.Max(1, columnIndex.Count);
        _rows = Math.Max(1, rowIndex.Count);
        foreach (var panel in Panels)
        {
            var columnSpan = Enumerable
                .Range(panel.LayoutColumn, Math.Max(1, panel.LayoutColumnSpan))
                .Count(usedColumns.Contains);
            var rowSpan = Enumerable
                .Range(panel.LayoutRow, Math.Max(1, panel.LayoutRowSpan))
                .Count(usedRows.Contains);
            panel.AssignLayout(
                _columns,
                _rows,
                new LayoutGridBounds(
                    columnIndex[panel.LayoutColumn],
                    rowIndex[panel.LayoutRow],
                    Math.Max(1, columnSpan),
                    Math.Max(1, rowSpan)),
                new LayoutMinimumSize(panel.LayoutMinimumWidth, panel.LayoutMinimumHeight));
        }

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(MinimumCanvasWidth));
        OnPropertyChanged(nameof(MinimumCanvasHeight));
    }

    /// <summary>
    /// Where the next created panel goes: the placeholder it replaces. Set when a
    /// placeholder is told what to be, so the panel lands in the cell the user
    /// chose rather than wherever the layout would have appended it.
    /// </summary>
    public PanelInstanceId? ReplaceTarget { get; set; }

    /// <summary>An empty cell, before it is anywhere.</summary>
    public static PanelPlaceholderViewModel NewPlaceholder() =>
        new(new PanelInstanceId($"placeholder-{Guid.NewGuid():n}"));

    /// <summary>
    /// Places an empty panel against one edge of the canvas.
    ///
    /// The caller may bring its own cell. A placed cell is part of the workspace
    /// graph, so the shell has to name it before proposing it and place this very
    /// one once the host agrees.
    /// </summary>
    public PanelPlaceholderViewModel AddPlaceholder(
        PanelSide side,
        PanelPlaceholderViewModel? placeholder = null)
    {
        ClearZoom();
        placeholder ??= NewPlaceholder();

        var column = side is PanelSide.Left or PanelSide.Right;
        var at = side switch
        {
            PanelSide.Left or PanelSide.Top => 0,
            PanelSide.Right => _columns,
            _ => _rows,
        };
        InsertTrack(column, at);

        placeholder.AssignLayout(
            _columns,
            _rows,
            column
                ? new LayoutGridBounds(at, 0, 1, _rows)
                : new LayoutGridBounds(0, at, _columns, 1),
            new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight));
        Panels.Add(placeholder);
        _dockLayout.AttachToEdge(placeholder, side);
        _usesAutomaticLayout = false;
        ActivatePanel(placeholder.Id);
        NotifyPanelLayoutChanged();
        return placeholder;
    }

    /// <summary>Places an empty panel beside an existing one.</summary>
    public PanelPlaceholderViewModel? SplitWithPlaceholder(
        PanelInstanceId panelId,
        PanelSplitOrientation orientation,
        PanelPlaceholderViewModel? placeholder = null)
    {
        var target = Panels.SingleOrDefault(panel => panel.Id == panelId);
        if (target is null)
        {
            return null;
        }

        ClearZoom();
        var column = orientation == PanelSplitOrientation.LeftRight;
        var start = column ? target.LayoutColumn : target.LayoutRow;
        var span = Math.Max(1, column ? target.LayoutColumnSpan : target.LayoutRowSpan);

        int placeholderStart;
        int placeholderSpan;
        if (span > 1)
        {
            // The panel already covers more than one track, so the boundary this
            // split needs is already there — dividing its span is the whole job.
            // Opening another track instead appended one at the end of the grid and
            // left the panel covering the larger share of it, which is how splitting
            // twice produced a half and two quarters rather than three thirds, and
            // how panels in other columns were pushed into overlapping each other.
            var kept = (span + 1) / 2;
            placeholderStart = start + kept;
            placeholderSpan = span - kept;
            AssignTrackSpan(target, column, start, kept);
        }
        else
        {
            placeholderStart = start + 1;
            placeholderSpan = 1;
            InsertTrack(column, placeholderStart, target);
        }

        placeholder ??= NewPlaceholder();
        placeholder.AssignLayout(
            _columns,
            _rows,
            column
                ? new LayoutGridBounds(
                    placeholderStart,
                    target.LayoutRow,
                    placeholderSpan,
                    Math.Max(1, target.LayoutRowSpan))
                : new LayoutGridBounds(
                    target.LayoutColumn,
                    placeholderStart,
                    Math.Max(1, target.LayoutColumnSpan),
                    placeholderSpan),
            new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight));
        Panels.Add(placeholder);
        _dockLayout.Attach(
            placeholder,
            split: orientation,
            targetPanelId: target.Id);
        _usesAutomaticLayout = false;
        ActivatePanel(placeholder.Id);
        NotifyPanelLayoutChanged();
        return placeholder;
    }

    /// <summary>Re-states one axis of a panel's cell, leaving the other alone.</summary>
    private void AssignTrackSpan(
        RuntimePanelViewModel panel,
        bool column,
        int start,
        int span) =>
        panel.AssignLayout(
            _columns,
            _rows,
            column
                ? new LayoutGridBounds(
                    start,
                    panel.LayoutRow,
                    span,
                    Math.Max(1, panel.LayoutRowSpan))
                : new LayoutGridBounds(
                    panel.LayoutColumn,
                    start,
                    Math.Max(1, panel.LayoutColumnSpan),
                    span),
            new LayoutMinimumSize(panel.LayoutMinimumWidth, panel.LayoutMinimumHeight));

    /// <summary>
    /// Opens a track at an index, moving everything at or after it along. A panel
    /// that straddles the insertion point grows rather than being torn in two.
    ///
    /// <paramref name="splitTarget"/> distinguishes the two reasons to open a track.
    /// Adding a panel against an edge of the canvas creates genuinely new space, and
    /// the panels already there keep the cells they had. Splitting one divides that
    /// panel's own cell, so the new track comes out of its area — and every other
    /// panel covering that area has to stretch across the new track instead of
    /// staying where it was. Without this, splitting a panel in a stacked layout
    /// pushed a full-height column through the whole grid and left a hole beside the
    /// panels that had shared its columns.
    /// </summary>
    private void InsertTrack(bool column, int at, RuntimePanelViewModel? splitTarget = null)
    {
        foreach (var panel in Panels)
        {
            var start = column ? panel.LayoutColumn : panel.LayoutRow;
            var span = Math.Max(1, column ? panel.LayoutColumnSpan : panel.LayoutRowSpan);
            int shifted;
            int grown;
            if (splitTarget is not null && ReferenceEquals(panel, splitTarget))
            {
                // The panel being split keeps its single track; the new one beside it
                // is the half it gave up.
                shifted = start;
                grown = span;
            }
            else if (splitTarget is not null)
            {
                // A panel reaching the new track's position covered that ground
                // before and has to go on covering it. One that stops short of it is
                // untouched, and one starting at or past it moves along — growing
                // every panel merely overlapping the split panel's rows made stacked
                // neighbours expand into each other.
                shifted = start >= at ? start + 1 : start;
                grown = start < at && start + span >= at ? span + 1 : span;
            }
            else
            {
                shifted = start >= at ? start + 1 : start;
                grown = start < at && at < start + span ? span + 1 : span;
            }

            panel.AssignLayout(
                column ? _columns + 1 : _columns,
                column ? _rows : _rows + 1,
                column
                    ? new LayoutGridBounds(shifted, panel.LayoutRow, grown, Math.Max(1, panel.LayoutRowSpan))
                    : new LayoutGridBounds(panel.LayoutColumn, shifted, Math.Max(1, panel.LayoutColumnSpan), grown),
                new LayoutMinimumSize(panel.LayoutMinimumWidth, panel.LayoutMinimumHeight));
        }

        if (column)
        {
            _columns++;
        }
        else
        {
            _rows++;
        }

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(MinimumCanvasWidth));
        OnPropertyChanged(nameof(MinimumCanvasHeight));
    }

    public void NotifyPanelLayoutChanged()
    {
        if (_usesAutomaticLayout)
        {
            ReflowAutomaticLayout();
            return;
        }

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(MinimumCanvasWidth));
        OnPropertyChanged(nameof(MinimumCanvasHeight));
    }

    public void DisposePanels()
    {
        foreach (var panel in Panels)
        {
            panel.Dispose();
        }

        ActivePanelId = null;
        ZoomedPanelId = null;
    }

    private void ClearZoom()
    {
        if (ZoomedPanelId is null)
        {
            return;
        }

        ZoomedPanelId = null;
        ApplyZoomState();
    }

    private void ApplyZoomState()
    {
        foreach (var panel in Panels)
        {
            panel.IsZoomed = panel.Id == ZoomedPanelId;
            panel.IsVisibleInLayout = ZoomedPanelId is null || panel.IsZoomed;
        }
    }

    private void CollapseRuntimeSplit(RuntimePanelViewModel removedPanel)
    {
        var splitIndex = _runtimeSplits.FindLastIndex(split => split.Contains(removedPanel.Id));
        if (splitIndex < 0)
        {
            return;
        }

        var split = _runtimeSplits[splitIndex];
        var siblingId = split.Other(removedPanel.Id);
        var siblingSubtreeIds = new HashSet<PanelInstanceId> { siblingId };
        for (var index = splitIndex + 1; index < _runtimeSplits.Count; index++)
        {
            var descendant = _runtimeSplits[index];
            if (siblingSubtreeIds.Contains(descendant.First)
                || siblingSubtreeIds.Contains(descendant.Second))
            {
                siblingSubtreeIds.Add(descendant.First);
                siblingSubtreeIds.Add(descendant.Second);
            }
        }

        var siblingSubtree = Panels
            .Where(panel => siblingSubtreeIds.Contains(panel.Id) && panel.Id != removedPanel.Id)
            .ToArray();
        _runtimeSplits.RemoveAt(splitIndex);
        if (siblingSubtree.Length == 0)
        {
            return;
        }

        var subtreeLeft = siblingSubtree.Min(panel => panel.LayoutColumn);
        var subtreeTop = siblingSubtree.Min(panel => panel.LayoutRow);
        var subtreeRight = siblingSubtree.Max(panel => panel.LayoutColumn + panel.LayoutColumnSpan);
        var subtreeBottom = siblingSubtree.Max(panel => panel.LayoutRow + panel.LayoutRowSpan);
        var left = Math.Min(removedPanel.LayoutColumn, subtreeLeft);
        var top = Math.Min(removedPanel.LayoutRow, subtreeTop);
        var right = Math.Max(
            removedPanel.LayoutColumn + removedPanel.LayoutColumnSpan,
            subtreeRight);
        var bottom = Math.Max(
            removedPanel.LayoutRow + removedPanel.LayoutRowSpan,
            subtreeBottom);
        var horizontalScale = split.Orientation == PanelSplitOrientation.LeftRight
            ? (right - left) / (subtreeRight - subtreeLeft)
            : 1;
        var verticalScale = split.Orientation == PanelSplitOrientation.TopBottom
            ? (bottom - top) / (subtreeBottom - subtreeTop)
            : 1;
        foreach (var sibling in siblingSubtree)
        {
            sibling.AssignLayout(
                _columns,
                _rows,
                new LayoutGridBounds(
                    left + ((sibling.LayoutColumn - subtreeLeft) * horizontalScale),
                    top + ((sibling.LayoutRow - subtreeTop) * verticalScale),
                    sibling.LayoutColumnSpan * horizontalScale,
                    sibling.LayoutRowSpan * verticalScale),
                new LayoutMinimumSize(sibling.LayoutMinimumWidth, sibling.LayoutMinimumHeight));
        }

        for (var index = 0; index < _runtimeSplits.Count; index++)
        {
            _runtimeSplits[index] = _runtimeSplits[index].Replace(removedPanel.Id, siblingId);
        }

        if (_runtimeSplits.Count == 0 && !_hasSavedLayout)
        {
            _usesAutomaticLayout = true;
        }
    }

    private static (double X, double Y) PanelCenter(RuntimePanelViewModel panel) => (
        panel.LayoutColumn + (panel.LayoutColumnSpan / 2d),
        panel.LayoutRow + (panel.LayoutRowSpan / 2d));

    private static bool IsInDirection(
        (double X, double Y) origin,
        (double X, double Y) candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left => candidate.X < origin.X,
            PanelFocusDirection.Right => candidate.X > origin.X,
            PanelFocusDirection.Up => candidate.Y < origin.Y,
            PanelFocusDirection.Down => candidate.Y > origin.Y,
            _ => false,
        };

    private static double PrimaryDistance(
        (double X, double Y) origin,
        (double X, double Y) candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right => Math.Abs(candidate.X - origin.X),
            PanelFocusDirection.Up or PanelFocusDirection.Down => Math.Abs(candidate.Y - origin.Y),
            _ => 0,
        };

    private static double CrossAxisDistance(
        (double X, double Y) origin,
        (double X, double Y) candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right => Math.Abs(candidate.Y - origin.Y),
            PanelFocusDirection.Up or PanelFocusDirection.Down => Math.Abs(candidate.X - origin.X),
            _ => 0,
        };

    private static bool CrossAxisOverlaps(
        RuntimePanelViewModel origin,
        RuntimePanelViewModel candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right =>
                origin.LayoutRow < candidate.LayoutRow + candidate.LayoutRowSpan
                && candidate.LayoutRow < origin.LayoutRow + origin.LayoutRowSpan,
            PanelFocusDirection.Up or PanelFocusDirection.Down =>
                origin.LayoutColumn < candidate.LayoutColumn + candidate.LayoutColumnSpan
                && candidate.LayoutColumn < origin.LayoutColumn + origin.LayoutColumnSpan,
            _ => false,
        };

    private void ReflowAutomaticLayout()
    {
        _columns = Panels.Count switch
        {
            <= 1 => 1,
            _ => 2,
        };
        _rows = Math.Max(1, (int)Math.Ceiling(Panels.Count / (double)_columns));
        for (var index = 0; index < Panels.Count; index++)
        {
            Panels[index].AssignLayout(
                _columns,
                _rows,
                new LayoutGridBounds(index % _columns, index / _columns, 1, 1),
                new LayoutMinimumSize(DefaultPanelMinimumWidth, DefaultPanelMinimumHeight));
        }

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(MinimumCanvasWidth));
        OnPropertyChanged(nameof(MinimumCanvasHeight));
    }

    private void UpdatePanelGridDimensions()
    {
        foreach (var panel in Panels)
        {
            panel.UpdateLayoutGrid(_columns, _rows);
        }
    }

    private enum TrackAxis
    {
        Columns,
        Rows,
    }

    private sealed record PanelLayoutSnapshot(
        RuntimePanelViewModel Panel,
        LayoutGridBounds Bounds,
        LayoutMinimumSize MinimumSize);

    private readonly record struct RuntimeSplitRelationship(
        PanelInstanceId First,
        PanelInstanceId Second,
        PanelSplitOrientation Orientation)
    {
        public bool Contains(PanelInstanceId panelId) => First == panelId || Second == panelId;

        public PanelInstanceId Other(PanelInstanceId panelId) => First == panelId ? Second : First;

        public RuntimeSplitRelationship Replace(PanelInstanceId oldId, PanelInstanceId newId) => new(
            First == oldId ? newId : First,
            Second == oldId ? newId : Second,
            Orientation);
    }
}

public abstract class RuntimePanelViewModel(
    PanelInstanceId id,
    PanelKind kind,
    string title,
    string kindLabel) : ObservableObject, IDisposable
{
    private bool _isActive;
    private bool _hasAttention;
    private bool _isVisibleInLayout = true;
    private bool _isZoomed;

    public PanelInstanceId Id { get; } = id;

    public PanelKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string KindLabel { get; } = kindLabel;

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Whether this asked to be noticed and has not been looked at since.
    ///
    /// Set from one place — the shell's notification centre — rather than
    /// computed here, for the same reason <see cref="IsActive"/> is: a flag
    /// that each level worked out for itself would need every level watching
    /// its children, and the levels already know nothing about each other.
    /// </summary>
    public bool HasAttention
    {
        get => _hasAttention;
        internal set => SetProperty(ref _hasAttention, value);
    }

    public bool IsVisibleInLayout
    {
        get => _isVisibleInLayout;
        internal set => SetProperty(ref _isVisibleInLayout, value);
    }

    public bool IsZoomed
    {
        get => _isZoomed;
        internal set => SetProperty(ref _isZoomed, value);
    }

    public int LayoutColumns { get; private set; } = 1;

    public int LayoutRows { get; private set; } = 1;

    public int LayoutColumn { get; private set; }

    public int LayoutRow { get; private set; }

    public int LayoutColumnSpan { get; private set; } = 1;

    public int LayoutRowSpan { get; private set; } = 1;

    public double LayoutMinimumWidth { get; private set; } = 220;

    public double LayoutMinimumHeight { get; private set; } = 140;

    public virtual void Dispose()
    {
    }

    internal void AssignLayout(
        int columns,
        int rows,
        LayoutGridBounds bounds,
        LayoutMinimumSize minimumSize)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(minimumSize);
        LayoutColumns = columns;
        LayoutRows = rows;
        LayoutColumn = bounds.Column;
        LayoutRow = bounds.Row;
        LayoutColumnSpan = bounds.ColumnSpan;
        LayoutRowSpan = bounds.RowSpan;
        LayoutMinimumWidth = minimumSize.Width;
        LayoutMinimumHeight = minimumSize.Height;
        NotifyLayoutChanged();
    }

    internal void UpdateLayoutGrid(int columns, int rows)
    {
        LayoutColumns = columns;
        LayoutRows = rows;
        OnPropertyChanged(nameof(LayoutColumns));
        OnPropertyChanged(nameof(LayoutRows));
    }

    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(LayoutColumns));
        OnPropertyChanged(nameof(LayoutRows));
        OnPropertyChanged(nameof(LayoutColumn));
        OnPropertyChanged(nameof(LayoutRow));
        OnPropertyChanged(nameof(LayoutColumnSpan));
        OnPropertyChanged(nameof(LayoutRowSpan));
        OnPropertyChanged(nameof(LayoutMinimumWidth));
        OnPropertyChanged(nameof(LayoutMinimumHeight));
    }
}

public enum ConnectionPanelState
{
    Planning,
    Reconnecting,
    Ready,
    Failed,
    CredentialBrokerRequired,
    Disposed,
}

public sealed class TerminalRuntimePanelViewModel : RuntimePanelViewModel, IPanelNotificationSource
{
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly IConnectionSecurityRuntime? _connectionSecurityRuntime;
    private readonly ConnectionProfile _connection;
    private readonly SessionOwner _owner;
    private TerminalRenderProfileSnapshot? _renderProfile;
    private readonly TerminalKeymapSnapshot? _keymap;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConnectionReconnectPolicy _reconnectPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelay;
    private IReadOnlyList<string> _startupCommands;
    private CancellationTokenSource? _attempt;
    private EnsureTerminalSessionRequest? _sessionRequest;
    private CancellationTokenSource? _notificationWatch;
    private bool _hasObservedActiveSession;
    private ConnectionPanelState _connectionState;
    private ConnectionRuntimeError? _connectionError;
    private string _connectionStatus = "Preparing connection";
    private string _connectionDetail = "Validating the saved connection profile…";
    private string? _warningMessage;
    private SshHostKeyReview? _hostKeyReview;
    private ConnectionReconnectState _reconnectState;
    private int _reconnectAttempt;
    private TimeSpan? _nextReconnectDelay;
    private ConnectionReconnectMode _reconnectMode;
    private TerminalStartupCommandDispatchError? _startupCommandError;
    private bool _startupCommandOutcomePinned;
    private bool _startupCommandFailureStopped;
    private bool _isCopyMode;
    private bool _isContinuityActive;
    private readonly TerminalMultiplexerCoordinator? _multiplexerCoordinator;
    private TerminalMultiplexerSession? _multiplexerSession;
    private bool _disposed;

    public TerminalRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IConnectionRuntime connectionRuntime,
        ConnectionProfile connection,
        SessionOwner owner,
        PanelStartupBehavior startup,
        TerminalRenderProfileSnapshot? renderProfile,
        ISessionHostClient sessionClient,
        ClientId clientId,
        TerminalStartupCommandDispatcher startupCommandDispatcher,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null,
        ConnectionReconnectPolicy? reconnectPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? reconnectDelay = null,
        TerminalKeymapSnapshot? keymap = null,
        PanelSessionRole sessionRole = PanelSessionRole.Primary,
        TerminalMultiplexerCoordinator? multiplexerCoordinator = null,
        TerminalMultiplexerSession? multiplexerSession = null)
        : base(
            id,
            PanelKind.Terminal,
            title,
            KindBadges.Connection(connection.ConnectionKind))
    {
        _connectionRuntime = connectionRuntime ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _connectionSecurityRuntime = connectionSecurityRuntime;
        _connection = WithPanelStartup(
            connection ?? throw new ArgumentNullException(nameof(connection)),
            startup ?? throw new ArgumentNullException(nameof(startup)));
        _owner = owner;
        _renderProfile = renderProfile;
        _keymap = keymap;
        _multiplexerCoordinator = multiplexerCoordinator;
        _multiplexerSession = multiplexerSession;
        SessionRole = sessionRole;
        _reconnectPolicy = reconnectPolicy ?? ConnectionReconnectPolicy.InteractiveDefault;
        _reconnectDelay = reconnectDelay ?? ((delay, token) => Task.Delay(delay, token));
        _reconnectMode = connection.ConnectionKind == ConnectionKind.Local
            ? ConnectionReconnectMode.NotApplicable
            : ConnectionReconnectMode.BoundedBackoff;
        _startupCommands = Array.AsReadOnly(startup.Commands.ToArray());
        SessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        ClientId = clientId;
        StartupCommandContext = OperationContext.ForHuman(
            ClientId,
            idempotencyKey: IdempotencyKey.New());
        StartupCommandDispatcher = startupCommandDispatcher
            ?? throw new ArgumentNullException(nameof(startupCommandDispatcher));
        StartupCommandDispatchState = new TerminalStartupCommandDispatchState(
            _owner.PanelId,
            _startupCommands,
            StartupCommandContext,
            failurePolicy: startup.DeliveryFailurePolicy);
        StartupCommandDispatchState.DispatchCompleted +=
            OnStartupCommandDispatchCompleted;
        Initialization = RetryAsync();
    }

    /// <summary>
    /// The typeface, size, and palette this panel renders with.
    ///
    /// A panel used to keep the snapshot it was launched with, so saving a new
    /// terminal size changed the stored profile and nothing on screen: every open
    /// panel kept rendering at the size it started at until it was closed and
    /// reopened.
    /// </summary>
    public TerminalRenderProfileSnapshot? RenderProfile
    {
        get => _renderProfile;
        internal set
        {
            if (value is null || value.RendersSameAs(_renderProfile))
            {
                return;
            }

            _renderProfile = value;
            OnPropertyChanged();
        }
    }

    public EnsureTerminalSessionRequest? SessionRequest
    {
        get => _sessionRequest;
        private set
        {
            if (!ReferenceEquals(_sessionRequest, value))
            {
                _sessionRequest = value;
                IsContinuityActive = false;
                HasObservedActiveSession = false;
                WatchNotifications(value?.SessionId);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConnectionOverlay));
            }
        }
    }

    /// <summary>
    /// Raised when the terminal asks to be noticed — a bell, or an OSC 9 /
    /// OSC 777 notification. Raised on whatever thread the stream delivers on;
    /// the shell marshals it.
    /// </summary>
    public event EventHandler<PanelNotificationEvent>? NotificationReceived;

    /// <summary>
    /// Follows one session's requests to be noticed.
    ///
    /// Per panel rather than per workspace on purpose: the workspace graph
    /// watch only runs for the workspace on screen, and a notification from a
    /// workspace nobody is looking at is the entire point of the feature.
    /// </summary>
    private void WatchNotifications(SessionId? sessionId)
    {
        var previous = Interlocked.Exchange(ref _notificationWatch, null);
        previous?.Cancel();
        previous?.Dispose();
        if (sessionId is not { } id || _disposed)
        {
            return;
        }

        var watch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _notificationWatch = watch;
        _ = WatchNotificationsAsync(id, watch.Token);
    }

    private async Task WatchNotificationsAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in SessionClient
                .WatchNotificationsAsync(
                    new WatchSessionRequest(sessionId, 0),
                    OperationContext.ForHuman(ClientId, idempotencyKey: IdempotencyKey.New()),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                if (ShouldRaise(notification))
                {
                    NotificationReceived?.Invoke(this, notification);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The panel closed or its session was replaced.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A notification stream that dies must not take the panel with it:
            // the terminal itself is unaffected by nobody listening for bells.
            Console.Error.WriteLine(
                $"[ghostshell:notifications] Watch for {sessionId} ended: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether a request to be noticed should reach the shell at all.
    ///
    /// The bell is the one the user has an opinion about, and until now that
    /// opinion had nowhere to land: the profile's bell mode was stored, edited,
    /// and read by nothing. A mode that asks only for a system alert is not
    /// asking for a mark on the tab.
    /// </summary>
    private bool ShouldRaise(PanelNotificationEvent notification) =>
        notification.Kind != PanelNotificationKind.Bell
        || (RenderProfile?.BellMode ?? TerminalBellMode.Visual) is
            TerminalBellMode.Visual or TerminalBellMode.SystemAndVisual;

    public bool HasObservedActiveSession
    {
        get => _hasObservedActiveSession;
        private set => SetProperty(ref _hasObservedActiveSession, value);
    }

    public ISessionHostClient SessionClient { get; }

    public PanelSessionRole SessionRole { get; }

    public ClientId ClientId { get; }

    public ConnectionId ConnectionId => _connection.Id;

    public ConnectionProfile Connection => _connection;

    public TerminalMultiplexerSession? MultiplexerSession
    {
        get => _multiplexerSession;
        private set => SetProperty(ref _multiplexerSession, value);
    }

    /// <summary>
    /// True only after the accepted launch carried a multiplexer identity and
    /// that exact terminal became healthy. Planned or recovered metadata alone
    /// is not evidence that continuity is active.
    /// </summary>
    public bool IsContinuityActive
    {
        get => _isContinuityActive;
        private set => SetProperty(ref _isContinuityActive, value);
    }

    public string ConnectionDisplayName =>
        _connection.Endpoint is ConnectionEndpoint.Local
            ? "Local"
            : _connection.Name;

    /// <summary>
    /// A logical location may be replayed after a crash. Startup commands are intentionally not
    /// exposed here because recovery must not repeat side effects whose delivery is uncertain.
    /// </summary>
    public string? RecoveryStartupLocation => _connection.Startup.Directory;

    /// <summary>
    /// The saved-screen batch identity outlives renderer recreation and reconnect attempts. The
    /// host fingerprint includes the session and command hash, so a possibly accepted
    /// write replays on the same session and is rejected rather than duplicated on a new one.
    /// </summary>
    public OperationContext StartupCommandContext { get; }

    public TerminalStartupCommandDispatcher StartupCommandDispatcher { get; }

    public TerminalStartupCommandDispatchState StartupCommandDispatchState { get; }

    /// <summary>
    /// The failure policy is pinned to this runtime panel's saved-screen definition instance.
    /// Replacing the durable definition affects future panels, not an already running terminal.
    /// </summary>
    public StartupCommandDeliveryFailurePolicy StartupCommandDeliveryFailurePolicy =>
        StartupCommandDispatchState.FailurePolicy;

    public bool IsCopyMode
    {
        get => _isCopyMode;
        private set => SetProperty(ref _isCopyMode, value);
    }

    public bool EnterCopyMode()
    {
        if (IsCopyMode)
        {
            return false;
        }

        IsCopyMode = true;
        return true;
    }

    public bool ExitCopyMode()
    {
        if (!IsCopyMode)
        {
            return false;
        }

        IsCopyMode = false;
        return true;
    }

    public IReadOnlyList<string> StartupCommands
    {
        get => _startupCommands;
        private set
        {
            if (!ReferenceEquals(_startupCommands, value))
            {
                _startupCommands = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartupCommandErrorDetail));
            }
        }
    }

    public Task Initialization { get; private set; }

    public TerminalStartupCommandDispatchError? StartupCommandError
    {
        get => _startupCommandError;
        private set
        {
            if (SetProperty(ref _startupCommandError, value))
            {
                OnPropertyChanged(nameof(HasStartupCommandError));
                OnPropertyChanged(nameof(StartupCommandErrorTitle));
                OnPropertyChanged(nameof(StartupCommandErrorDetail));
            }
        }
    }

    public bool HasStartupCommandError => StartupCommandError is not null;

    public string StartupCommandErrorTitle => StartupCommandError?.Code ==
        TerminalStartupCommandDispatchErrorCode.AuditPersistenceFailure
            ? "Startup command audit unavailable"
            : "Startup commands not confirmed";

    public string StartupCommandErrorDetail
    {
        get
        {
            if (StartupCommandError is not { } error)
            {
                return string.Empty;
            }

            if (_startupCommandFailureStopped)
            {
                return $"{error.Message} The saved startup commands will not be retried automatically. The terminal remains open.";
            }

            return error.Retryable && StartupCommands.Count > 0
                ? $"{error.Message} Retrying while this session remains live."
                : error.Message;
        }
    }

    public ConnectionPanelState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (_connectionState != value)
            {
                _connectionState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(HasConnectionOverlay));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public ConnectionRuntimeError? ConnectionError
    {
        get => _connectionError;
        private set
        {
            if (!Equals(_connectionError, value))
            {
                _connectionError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(RecoveryLabel));
            }
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string ConnectionDetail
    {
        get => _connectionDetail;
        private set => SetProperty(ref _connectionDetail, value);
    }

    public string? WarningMessage
    {
        get => _warningMessage;
        private set
        {
            if (SetProperty(ref _warningMessage, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public SshHostKeyReview? HostKeyReview
    {
        get => _hostKeyReview;
        private set
        {
            if (SetProperty(ref _hostKeyReview, value))
            {
                OnPropertyChanged(nameof(HasHostKeyReview));
                OnPropertyChanged(nameof(HostKeyReviewTitle));
                OnPropertyChanged(nameof(TrustedHostKeyFingerprint));
                OnPropertyChanged(nameof(TrustHostKeyLabel));
                OnPropertyChanged(nameof(CanTrustHostKey));
            }
        }
    }

    public ConnectionReconnectState ReconnectState
    {
        get => _reconnectState;
        private set
        {
            if (SetProperty(ref _reconnectState, value))
            {
                OnPropertyChanged(nameof(IsReconnecting));
                OnPropertyChanged(nameof(CanCancelReconnect));
                OnPropertyChanged(nameof(ReconnectStatus));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public int ReconnectAttempt
    {
        get => _reconnectAttempt;
        private set
        {
            if (SetProperty(ref _reconnectAttempt, value))
            {
                OnPropertyChanged(nameof(ReconnectStatus));
            }
        }
    }

    public TimeSpan? NextReconnectDelay
    {
        get => _nextReconnectDelay;
        private set
        {
            if (SetProperty(ref _nextReconnectDelay, value))
            {
                OnPropertyChanged(nameof(ReconnectStatus));
            }
        }
    }

    public bool IsConnecting => ConnectionState is
        ConnectionPanelState.Planning or ConnectionPanelState.Reconnecting;

    public bool IsReconnecting => ReconnectState is
        ConnectionReconnectState.Waiting or
        ConnectionReconnectState.Attempting or
        ConnectionReconnectState.WaitingForSession;

    public bool HasConnectionOverlay => SessionRequest is null && !_disposed;

    public bool CanRetry => ConnectionState == ConnectionPanelState.Failed
        && ConnectionError is { } error
        && (error.Retryable
            || ReconnectState is ConnectionReconnectState.Cancelled or ConnectionReconnectState.Exhausted
            || error.RecoveryAction is ConnectionRecoveryAction.InstallRuntime
                or ConnectionRecoveryAction.UnlockSecretVault
                or ConnectionRecoveryAction.GrantPermission);

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public bool HasHostKeyReview => HostKeyReview is not null;

    public string HostKeyReviewTitle => HostKeyReview?.Disposition switch
    {
        SshHostKeyDisposition.Unknown => "Unknown SSH host key",
        SshHostKeyDisposition.Changed => "SSH host key changed",
        SshHostKeyDisposition.Trusted => "SSH host key trusted",
        _ => string.Empty,
    };

    public string TrustedHostKeyFingerprint => HostKeyReview?.Trusted?.Sha256Fingerprint
        ?? "No previously trusted key";

    public string TrustHostKeyLabel => HostKeyReview?.Disposition == SshHostKeyDisposition.Changed
        ? "Replace trusted key…"
        : "Trust host key…";

    public bool CanTrustHostKey => HostKeyReview?.Disposition is
        SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed;

    public bool CanCancelReconnect => ReconnectState is
        ConnectionReconnectState.Waiting or ConnectionReconnectState.Attempting;

    public string ReconnectStatus => ReconnectState switch
    {
        ConnectionReconnectState.Waiting when NextReconnectDelay is { } delay =>
            $"Reconnect {ReconnectAttempt}/{_reconnectPolicy.MaximumAttempts} in {delay.TotalSeconds:0.#}s",
        ConnectionReconnectState.Attempting =>
            $"Reconnect {ReconnectAttempt}/{_reconnectPolicy.MaximumAttempts} in progress",
        ConnectionReconnectState.WaitingForSession => "Waiting for the reconnected terminal",
        ConnectionReconnectState.Connected => "Connection restored",
        ConnectionReconnectState.Exhausted => "Automatic reconnect attempts exhausted",
        ConnectionReconnectState.Cancelled => "Automatic reconnect cancelled",
        _ => string.Empty,
    };

    public string RecoveryLabel => ConnectionError?.RecoveryAction switch
    {
        ConnectionRecoveryAction.InstallRuntime => "Install the required runtime, then retry.",
        ConnectionRecoveryAction.UnlockSecretVault => "Unlock the credential vault, then retry.",
        ConnectionRecoveryAction.ProvideAuthentication => "Update authentication in the connection profile.",
        ConnectionRecoveryAction.ReviewHostKey => "Review the remote host key before reconnecting.",
        ConnectionRecoveryAction.GrantPermission => "Grant the required operating-system permission.",
        ConnectionRecoveryAction.SelectContainer => "Choose a running container in the connection profile.",
        ConnectionRecoveryAction.SelectDistribution => "Choose an installed WSL distribution.",
        ConnectionRecoveryAction.EditProfile => "Repair the saved connection profile.",
        ConnectionRecoveryAction.Retry or ConnectionRecoveryAction.Reconnect => "Retry the connection.",
        _ => string.Empty,
    };

    public Task RetryAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnecting && _attempt is not null)
        {
            return Initialization;
        }

        ReconnectAttempt = 0;
        ReconnectState = ConnectionReconnectState.Idle;
        NextReconnectDelay = null;
        return StartConnectionLoop(waitBeforeFirstAttempt: false);
    }

    public void CancelReconnect()
    {
        if (!CanCancelReconnect)
        {
            return;
        }

        _attempt?.Cancel();
        SessionRequest = null;
        ConnectionError = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled);
        ReconnectState = ConnectionReconnectState.Cancelled;
        NextReconnectDelay = null;
        ConnectionState = ConnectionPanelState.Failed;
        ConnectionStatus = "Reconnect cancelled";
        ConnectionDetail = "The terminal remains disconnected. Retry when you are ready.";
    }

    public async Task TrustHostKeyAsync(CancellationToken cancellationToken)
    {
        if (_connectionSecurityRuntime is null || HostKeyReview is not { } review)
        {
            return;
        }

        var action = review.RequiresExplicitReplacement
            ? SshHostKeyTrustAction.ReplaceChanged
            : SshHostKeyTrustAction.TrustNew;
        var result = await _connectionSecurityRuntime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, review.ConnectionId, action),
            cancellationToken);
        if (result is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
        {
            ConnectionError = failure.Error;
            ConnectionStatus = "Host key not trusted";
            ConnectionDetail = failure.Error.Message;
            return;
        }

        HostKeyReview = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)result).Value;
        await RetryAsync();
    }

    public void ObserveSessionSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed || SessionRequest?.SessionId != snapshot.Descriptor.Id)
        {
            return;
        }

        var isExactActiveSession =
            snapshot.Descriptor.Kind == PanelKind.Terminal
            && snapshot.Descriptor.Owner == _owner
            && snapshot.Descriptor.Lifecycle == SessionLifecycle.Active;
        HasObservedActiveSession = isExactActiveSession;
        if (isExactActiveSession
            && snapshot.Descriptor.Health == SessionHealth.Healthy)
        {
            if (SessionRequest.Launch.MultiplexerSession is { } launchedMultiplexer)
            {
                MultiplexerSession = launchedMultiplexer.IsEstablished
                    ? launchedMultiplexer
                    : launchedMultiplexer.MarkEstablished();
                IsContinuityActive = true;
                if (_multiplexerCoordinator is not null)
                {
                    _ = _multiplexerCoordinator.RegisterAsync(
                        _connection,
                        MultiplexerSession,
                        CancellationToken.None);
                }
            }
            else
            {
                // A runtime adapter may decline a requested launch feature.
                // Once its direct launch is accepted, stale intent must not be
                // persisted or presented as an established multiplexer.
                MultiplexerSession = null;
                IsContinuityActive = false;
            }
            ReconnectAttempt = 0;
            NextReconnectDelay = null;
            ReconnectState = ConnectionReconnectState.Connected;
            ConnectionState = ConnectionPanelState.Ready;
            ConnectionStatus = "Connected";
            ConnectionDetail = "The terminal session is live.";
            return;
        }

        if (snapshot.Descriptor.Lifecycle == SessionLifecycle.Failed)
        {
            var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed) with
            {
                Retryable = snapshot.Descriptor.Failure?.Retryable ?? true,
            };
            BeginAutomaticReconnect(error);
            return;
        }

        if (snapshot.Descriptor.Lifecycle == SessionLifecycle.Closed)
        {
            SessionRequest = null;
            ConnectionError = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed);
            ConnectionState = ConnectionPanelState.Failed;
            ReconnectState = ConnectionReconnectState.Idle;
            ConnectionStatus = "Session ended";
            ConnectionDetail = "The terminal process exited. Reconnect manually to start a new session.";
        }
    }

    public void ObserveSessionInitializationFailure(SessionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (_disposed)
        {
            return;
        }

        var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed) with
        {
            Retryable = failure.Retryable,
        };
        BeginAutomaticReconnect(error);
    }

    public void ObserveStartupCommandDispatch(TerminalStartupCommandDispatchResult result)
    {
        ObserveStartupCommandDispatch(StartupCommandContext, result);
    }

    public void ObserveStartupCommandDispatch(
        OperationContext context,
        TerminalStartupCommandDispatchResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed
            || context.RequestId != StartupCommandContext.RequestId
            || context.IdempotencyKey != StartupCommandContext.IdempotencyKey)
        {
            return;
        }

        var mustStopAfterFailure = result.Error is { } error
            && (!error.Retryable
                || StartupCommandDeliveryFailurePolicy
                    == StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var outcomePinned = result.CommandsDelivered || mustStopAfterFailure;
        if (outcomePinned)
        {
            // This VM outlives replaceable renderers. Withdrawing the batch here keeps a completed
            // or terminally failed command delivery one-shot across reattach and reconnect.
            _startupCommandOutcomePinned = true;
            _startupCommandFailureStopped =
                mustStopAfterFailure && !result.CommandsDelivered;
            StartupCommands = [];
        }

        StartupCommandError = result.Error;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StartupCommandDispatchState.DispatchCompleted -=
            OnStartupCommandDispatchCompleted;
        StartupCommandDispatchState.Dispose();
        _lifetime.Cancel();
        _attempt?.Cancel();
        _attempt?.Dispose();
        _lifetime.Dispose();
        SessionRequest = null;
        ConnectionState = ConnectionPanelState.Disposed;
    }

    private void OnStartupCommandDispatchCompleted(
        object? sender,
        TerminalStartupCommandDispatchEventArgs eventArgs)
    {
        _ = sender;
        ObserveStartupCommandDispatch(eventArgs.Context, eventArgs.Result);
    }

    private Task StartConnectionLoop(bool waitBeforeFirstAttempt)
    {
        _attempt?.Cancel();
        _attempt?.Dispose();
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SessionRequest = null;
        ConnectionError = null;
        if (!_startupCommandOutcomePinned)
        {
            StartupCommandError = null;
        }

        HostKeyReview = null;
        WarningMessage = null;
        ConnectionState = ConnectionPanelState.Planning;
        ConnectionStatus = "Preparing connection";
        ConnectionDetail = "Validating the saved connection profile…";
        Initialization = RunConnectionLoopAsync(
            _attempt,
            waitBeforeFirstAttempt,
            _attempt.Token);
        return Initialization;
    }

    private async Task RunConnectionLoopAsync(
        CancellationTokenSource attempt,
        bool waitBeforeFirstAttempt,
        CancellationToken cancellationToken)
    {
        var mustWait = waitBeforeFirstAttempt;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (mustWait)
            {
                if (ReconnectAttempt >= _reconnectPolicy.MaximumAttempts)
                {
                    ReconnectState = ConnectionReconnectState.Exhausted;
                    NextReconnectDelay = null;
                    ConnectionState = ConnectionPanelState.Failed;
                    ConnectionStatus = "Connection unavailable";
                    ConnectionDetail = "Automatic reconnect attempts were exhausted.";
                    return;
                }

                ReconnectAttempt++;
                NextReconnectDelay = _reconnectPolicy.DelayForAttempt(ReconnectAttempt);
                ReconnectState = ConnectionReconnectState.Waiting;
                ConnectionState = ConnectionPanelState.Reconnecting;
                ConnectionStatus = "Waiting to reconnect";
                ConnectionDetail = ReconnectStatus;
                try
                {
                    await _reconnectDelay(NextReconnectDelay.Value, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ReconnectState = ConnectionReconnectState.Attempting;
                NextReconnectDelay = null;
                ConnectionStatus = "Reconnecting";
                ConnectionDetail = ReconnectStatus;
            }

            var outcome = await PrepareSessionAsync(attempt, cancellationToken);
            if (!ReferenceEquals(_attempt, attempt) || _disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (outcome.Request is { } request)
            {
                SessionRequest = request;
                ConnectionState = ConnectionPanelState.Ready;
                ReconnectState = ReconnectAttempt > 0
                    ? ConnectionReconnectState.WaitingForSession
                    : ConnectionReconnectState.Idle;
                ConnectionStatus = ReconnectAttempt > 0 ? "Starting reconnected terminal" : "Starting terminal";
                ConnectionDetail = "The connection plan is ready.";
                return;
            }

            if (outcome.CredentialBrokerRequired)
            {
                ConnectionState = ConnectionPanelState.CredentialBrokerRequired;
                ReconnectState = ConnectionReconnectState.Idle;
                ConnectionStatus = "Credential delivery unavailable";
                ConnectionDetail = "The saved credential passed vault preflight, but this build cannot deliver it to an SSH terminal process without exposing secret material. Diagnostics can still authenticate through the secure adapter boundary.";
                return;
            }

            ConnectionError = outcome.Error;
            ConnectionState = ConnectionPanelState.Failed;
            ConnectionStatus = "Connection unavailable";
            ConnectionDetail = outcome.Error?.Message ?? "The connection could not be prepared.";
            if (outcome.Error is null || !ShouldReconnect(outcome.Error))
            {
                ReconnectState = ConnectionReconnectState.Idle;
                return;
            }

            mustWait = true;
        }
    }

    private async Task<ConnectionAttemptOutcome> PrepareSessionAsync(
        CancellationTokenSource attempt,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<ConnectionProgress>(item =>
        {
            if (!ReferenceEquals(_attempt, attempt)
                || _disposed
                || ConnectionState is not (ConnectionPanelState.Planning or ConnectionPanelState.Reconnecting))
            {
                return;
            }

            ConnectionStatus = ProgressTitle(item.Stage);
            ConnectionDetail = item.Message;
        });

        var hostKeyError = await PrepareHostKeyAsync(progress, cancellationToken);
        if (hostKeyError is not null)
        {
            return ConnectionAttemptOutcome.Fail(hostKeyError);
        }

        ConnectionRuntimeResult<ConnectionOpenPlan> result;
        try
        {
            result = await _connectionRuntime.PlanOpenAsync(
                _connection,
                MultiplexerSession,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConnectionAttemptOutcome.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled));
        }
        catch (Exception)
        {
            result = ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed));
        }

        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
        {
            return ConnectionAttemptOutcome.Fail(failure.Error);
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
        _reconnectMode = plan.ReconnectMode;
        WarningMessage = DescribeWarnings(plan.Warnings);
        if (plan.RequiresSecretBroker)
        {
            return ConnectionAttemptOutcome.BrokerRequired();
        }

        var launch = plan.Launch.WithPresentationProfiles(
            _renderProfile,
            _keymap);
        var startupCommands = StartupCommandDispatchState.Commands;
        if (startupCommands.Count == 1
            && DockerContainerShellCommand.IsContainerShellCommand(startupCommands[0]))
        {
            launch = launch.WithShellActivityFallback(
                TerminalShellActivityFallback.PromptShape);
        }

        return ConnectionAttemptOutcome.Succeed(new EnsureTerminalSessionRequest(
            SessionId.New(),
            _owner,
            Title,
            launch,
            SessionRole));
    }

    private async Task<ConnectionRuntimeError?> PrepareHostKeyAsync(
        IProgress<ConnectionProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_connectionSecurityRuntime is null
            || _connection.Endpoint is not ConnectionEndpoint.Ssh
            || _connection.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore)
        {
            return null;
        }

        var inspected = await _connectionSecurityRuntime.InspectSshHostKeyAsync(
            _connection,
            progress,
            cancellationToken);
        if (inspected is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
        {
            return failure.Error;
        }

        var review = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)inspected).Value;
        if (review.Disposition == SshHostKeyDisposition.Unknown
            && _connection.HostKeyPolicy == SshHostKeyPolicy.AcceptNew)
        {
            var trusted = await _connectionSecurityRuntime.TrustSshHostKeyAsync(
                new SshHostKeyTrustRequest(
                    review.Id,
                    review.ConnectionId,
                    SshHostKeyTrustAction.TrustNew),
                cancellationToken);
            return trusted is ConnectionRuntimeResult<SshHostKeyReview>.Failure trustFailure
                ? trustFailure.Error
                : null;
        }

        HostKeyReview = review.Disposition is SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed
            ? review
            : null;
        return review.Disposition switch
        {
            SshHostKeyDisposition.Unknown =>
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.UnknownHostKey),
            SshHostKeyDisposition.Changed =>
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.HostKeyChanged),
            _ => null,
        };
    }

    private void BeginAutomaticReconnect(ConnectionRuntimeError error)
    {
        SessionRequest = null;
        ConnectionError = error;
        if (!ShouldReconnect(error))
        {
            ConnectionState = ConnectionPanelState.Failed;
            ConnectionStatus = "Connection unavailable";
            ConnectionDetail = error.Message;
            return;
        }

        _ = StartConnectionLoop(waitBeforeFirstAttempt: true);
    }

    private bool ShouldReconnect(ConnectionRuntimeError error) =>
        _reconnectMode == ConnectionReconnectMode.BoundedBackoff
        && error.Retryable
        && error.RecoveryAction is ConnectionRecoveryAction.Retry or ConnectionRecoveryAction.Reconnect;

    private static ConnectionProfile WithPanelStartup(
        ConnectionProfile connection,
        PanelStartupBehavior startup) =>
        new(
            connection.Id,
            connection.SchemaVersion,
            connection.Name,
            connection.Endpoint,
            connection.Authentication,
            new ConnectionStartup(
                startup.Location ?? connection.Startup.Directory,
                connection.Startup.Environment),
            connection.KeepAlive,
            connection.HostKeyPolicy,
            connection.Tags);

    private static string ProgressTitle(ConnectionProgressStage stage) => stage switch
    {
        ConnectionProgressStage.ValidatingProfile => "Validating connection",
        ConnectionProgressStage.DetectingRuntime => "Detecting runtime",
        ConnectionProgressStage.ResolvingCredentials => "Checking credentials",
        ConnectionProgressStage.BuildingLaunchPlan => "Preparing terminal",
        ConnectionProgressStage.InspectingHostKey => "Inspecting host key",
        ConnectionProgressStage.Authenticating => "Authenticating",
        ConnectionProgressStage.ProbingEndpoint => "Testing endpoint",
        ConnectionProgressStage.Reconnecting => "Reconnecting",
        ConnectionProgressStage.Completed => "Connection prepared",
        _ => "Preparing connection",
    };

    private static string? DescribeWarnings(IReadOnlyList<ConnectionPlanWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        var messages = warnings.Select(warning => warning switch
        {
            ConnectionPlanWarning.HostKeyVerificationDisabled =>
                "SSH host-key verification is disabled.",
            ConnectionPlanWarning.SecretBrokerRequired =>
                "This connection requires the secure credential broker.",
            ConnectionPlanWarning.RemoteEnvironmentRequiresServerAcceptance =>
                "SSH environment forwarding requires matching AcceptEnv rules on the remote server.",
            ConnectionPlanWarning.SshStartupDirectoryRequiresPosixShell =>
                "SSH startup directories require a POSIX-compatible remote target with /bin/sh; Windows OpenSSH targets are not supported for this option.",
            _ => throw new ArgumentOutOfRangeException(nameof(warnings), warning, null),
        });
        return string.Join(' ', messages);
    }

    private sealed record ConnectionAttemptOutcome(
        EnsureTerminalSessionRequest? Request,
        ConnectionRuntimeError? Error,
        bool CredentialBrokerRequired)
    {
        public static ConnectionAttemptOutcome Succeed(EnsureTerminalSessionRequest request) =>
            new(request, null, false);

        public static ConnectionAttemptOutcome Fail(ConnectionRuntimeError error) =>
            new(null, error, false);

        public static ConnectionAttemptOutcome BrokerRequired() => new(null, null, true);
    }
}

public sealed class UnavailableRuntimePanelViewModel(
    PanelInstanceId id,
    PanelKind kind,
    string title,
    string kindLabel,
    string capabilityMessage)
    : RuntimePanelViewModel(id, kind, title, kindLabel)
{
    public string CapabilityMessage { get; } = capabilityMessage;
}

/// <summary>
/// The window-chrome half of a theme, passed separately so a caller that does
/// not present these settings can leave the stored values untouched.
/// </summary>
public sealed record ThemeChromePreference(
    InterfaceDensity Density,
    bool ShowTabBar,
    bool ShowWorkspacesPanel,
    TabStripPlacement TabStripPlacement,
    WorkspacePanelPlacement WorkspacePanelPlacement,
    bool IsTranslucent,
    int BackdropOpacityPercent,
    bool HasGlassPanels,
    bool OverridesBackdropOpacity)
{
    public static ThemeChromePreference From(ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(
            theme.Density,
            theme.ShowTabBar,
            theme.ShowWorkspacesPanel,
            theme.TabStripPlacement,
            theme.WorkspacePanelPlacement,
            theme.IsTranslucent,
            theme.BackdropOpacityPercent,
            theme.HasGlassPanels,
            theme.OverridesBackdropOpacity);
    }
}
