using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// A generic multi-driver database viewer: pick a driver, connect with an
/// ADO.NET connection string, browse tables, and run bounded queries. All
/// engine specifics stay behind <see cref="IDatabasePanelClient"/>; the panel
/// holds no open connection between operations.
/// </summary>
public sealed class DatabaseRuntimePanelViewModel : RuntimePanelViewModel
{
    private enum DatabaseResultSource
    {
        None,
        StructuredTable,
        RawQuery,
    }

    /// <summary>Result sets are display pages, not exports; the cap keeps the grid honest.</summary>
    public const int MaxRows = 500;

    /// <summary>Largest page the database browser will materialize into the grid.</summary>
    public const int MaximumPageRows = 5000;

    /// <summary>Maximum UTF-8 payload returned by a clipboard-oriented string builder.</summary>
    public const int MaximumClipboardUtf8Bytes = DatabaseGridExport.MaximumClipboardUtf8Bytes;

    private const int PreviewRows = 200;
    private const int MaximumFilterListValues = 500;
    private const int MaximumFilterListCharacters = 64 * 1024;

    private readonly IDatabasePanelClient _client;
    private readonly Func<SecretRef, CancellationToken, Task<string?>>? _passwordResolver;
    private readonly string? _forcedReadOnlyReason;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private ConnectionProfile? _tunnelConnection;
    private IReadOnlyList<string> _databases = [];
    private string? _selectedDatabase;
    private bool _suppressDatabaseSwitch;
    private DatabaseSessionInfo _sessionInfo = new();
    private bool _isPersistedConnection = true;
    private DatabaseConnectionProfile? _savedConnection;
    private string? _sessionPassword;
    private DatabaseDriverOptionViewModel _selectedDriver;
    private string _connectionString = string.Empty;
    private string _queryText = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private string? _errorMessage;
    private string _resultSummary = string.Empty;
    private IReadOnlyList<DatabaseTableItemViewModel> _allTables = [];
    private string _tableFilter = string.Empty;
    private IReadOnlyList<DatabaseResultColumnViewModel> _resultColumns = [];
    private IReadOnlyList<DatabaseResultRowViewModel> _resultRows = [];
    private DatabaseResultRowViewModel? _selectedRow;
    private IReadOnlyList<DatabaseRowFieldViewModel> _selectedRowFields = [];
    private DatabaseTableItemViewModel? _selectedObject;
    private DatabaseObjectDetails? _selectedObjectDetails;
    private DatabaseObjectDetails? _queryProvenanceCandidate;
    private DatabaseWorkspaceMode _selectedMode;
    private IReadOnlyList<DatabaseStructureColumnViewModel> _structureColumns = [];
    private IReadOnlyList<DatabaseIndexViewModel> _indexes = [];
    private IReadOnlyList<DatabaseFilterColumnViewModel> _filterColumns = [];
    private IReadOnlyList<DatabaseFilterOperatorViewModel> _filterOperators =
        AllFilterOperators;
    private DatabaseFilterColumnViewModel? _filterColumn;
    private DatabaseFilterOperatorViewModel? _filterOperator;
    private string _filterValue = string.Empty;
    private DatabaseTableQuery _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
    private DatabaseResultSource _resultSource;
    private string? _rawQuerySql;
    private IReadOnlyList<DatabaseColumnDescriptor> _rawQueryColumns = [];
    private bool _rawQueryCanBrowse;
    private bool _hasNextPage;
    private long _totalRows;
    private string _pageLimitText = PreviewRows.ToString(CultureInfo.InvariantCulture);
    private readonly List<DatabaseResultRowViewModel> _deletedRows = [];
    private CancellationTokenSource? _tableLoadCancellation;
    private long _tableLoadGeneration;

    private static readonly IReadOnlyList<DatabaseFilterOperatorViewModel> AllFilterOperators =
    [
        new(DatabaseFilterOperator.Equal, "Equals"),
        new(DatabaseFilterOperator.NotEqual, "Does not equal"),
        new(DatabaseFilterOperator.LessThan, "Less than"),
        new(DatabaseFilterOperator.GreaterThan, "Greater than"),
        new(DatabaseFilterOperator.LessThanOrEqual, "At most"),
        new(DatabaseFilterOperator.GreaterThanOrEqual, "At least"),
        new(DatabaseFilterOperator.Contains, "Contains"),
        new(DatabaseFilterOperator.NotContains, "Does not contain"),
        new(DatabaseFilterOperator.StartsWith, "Starts with"),
        new(DatabaseFilterOperator.EndsWith, "Ends with"),
        new(DatabaseFilterOperator.In, "In"),
        new(DatabaseFilterOperator.NotIn, "Not in"),
        new(DatabaseFilterOperator.IsNull, "Is NULL"),
        new(DatabaseFilterOperator.IsNotNull, "Is not NULL"),
    ];

    public DatabaseRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IDatabasePanelClient client,
        string? driverId = null,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        Func<SecretRef, CancellationToken, Task<string?>>? passwordResolver = null,
        string? forcedReadOnlyReason = null)
        : base(id, PanelKind.DatabaseViewer, title, "Database")
    {
        _tunnelConnection = tunnelConnection?.Endpoint is ConnectionEndpoint.Ssh
            ? tunnelConnection
            : null;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _passwordResolver = passwordResolver;
        _forcedReadOnlyReason = string.IsNullOrWhiteSpace(forcedReadOnlyReason)
            ? null
            : forcedReadOnlyReason;
        DriverOptions = client.Drivers
            .Select(descriptor => new DatabaseDriverOptionViewModel(descriptor))
            .ToArray();
        if (DriverOptions.Count == 0)
        {
            throw new ArgumentException(
                "The database client exposes no drivers.",
                nameof(client));
        }

        _savedConnection = savedConnection;
        var effectiveDriverId = savedConnection?.DriverId ?? driverId;
        _selectedDriver = DriverOptions.FirstOrDefault(option =>
                string.Equals(option.Id, effectiveDriverId, StringComparison.Ordinal))
            ?? DriverOptions[0];
        _connectionString = savedConnection?.ConnectionString ?? connectionString ?? string.Empty;
        ConnectCommand = new AsyncActionCommand(
            ConnectAsync,
            () => CanChangeConnection && HasConnectionTarget);
        DisconnectCommand = new AsyncActionCommand(
            () =>
            {
                Disconnect();
                return Task.CompletedTask;
            },
            () => IsConnected && CanChangeConnection);
        RunQueryCommand = new AsyncActionCommand(
            RunQueryAsync,
            () => !IsBusy && IsConnected && !HasPendingChanges);
        // A restored panel reconnects on its own: the saved target is the whole
        // point of persisting it. A saved connection that must ask for its
        // password waits for the user instead — the prompt needs a view.
        Initialization = !string.IsNullOrWhiteSpace(_connectionString)
            && (savedConnection is not null || driverId is not null)
            && !NeedsPasswordPrompt
            ? ConnectAsync()
            : Task.CompletedTask;
    }

    /// <summary>Raised when connecting needs a password only the user can supply.</summary>
    public event EventHandler? PasswordRequested;

    public bool IsSavedConnection => _savedConnection is not null;

    public DatabaseConnectionProfileId? SavedConnectionId => _savedConnection?.Id;

    public string? SavedConnectionName => _savedConnection?.Name;

    /// <summary>What the address bar shows: the saved name, or the masked string.</summary>
    public string AddressBarText => _savedConnection?.Name ?? MaskedConnectionString;

    /// <summary>
    /// Binds this panel to a connection profile: driver, address, and tunnel
    /// become the profile's, and connecting resolves the stored password — or
    /// asks for one. A session password supplied by the editor avoids
    /// re-asking for what the user just typed. A non-persisted profile (the
    /// editor's "connect without saving") behaves identically but recovers as
    /// a raw target rather than a dangling saved reference.
    /// </summary>
    public void ApplySavedConnection(
        DatabaseConnectionProfile profile,
        string? sessionPassword = null,
        ConnectionProfile? tunnel = null,
        bool persisted = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection.";
            return;
        }

        _savedConnection = profile;
        _isPersistedConnection = persisted;
        _sessionPassword = string.IsNullOrEmpty(sessionPassword) ? null : sessionPassword;
        _tunnelConnection = tunnel?.Endpoint is ConnectionEndpoint.Ssh ? tunnel : null;
        var driver = DriverOptions.FirstOrDefault(option =>
            string.Equals(option.Id, profile.DriverId, StringComparison.Ordinal));
        if (driver is not null)
        {
            _selectedDriver = driver;
            OnPropertyChanged(nameof(SelectedDriver));
        }

        SetDatabases([]);
        SessionInfo = new DatabaseSessionInfo();
        ConnectionString = profile.ConnectionString;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(SavedConnectionName));
        OnPropertyChanged(nameof(AddressBarText));
        OnPropertyChanged(nameof(RecoveryTarget));
        OnPropertyChanged(nameof(TunnelConnectionId));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        _ = ConnectAsync();
    }

    /// <summary>The prompt's answer; an empty value means connect without one.</summary>
    public void SetSessionPassword(string password) =>
        _sessionPassword = password ?? string.Empty;

    private bool NeedsPasswordPrompt =>
        _savedConnection is not null
        && !SelectedDriver.IsFileBased
        && _savedConnection.PasswordSecret is null
        && _sessionPassword is null
        && _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString).Password is null;

    /// <summary>
    /// The string handed to the engine: a saved connection gets its password
    /// injected from the session or the vault; everything else passes through.
    /// </summary>
    private async Task<string> ResolveEffectiveConnectionStringAsync(
        CancellationToken cancellationToken)
    {
        if (_savedConnection is null || SelectedDriver.IsFileBased)
        {
            return ConnectionString;
        }

        var password = _sessionPassword;
        if (string.IsNullOrEmpty(password)
            && _savedConnection.PasswordSecret is { } secret
            && _passwordResolver is not null)
        {
            password = await _passwordResolver(secret, cancellationToken);
        }

        if (string.IsNullOrEmpty(password))
        {
            return ConnectionString;
        }

        var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
        return details.Password is null
            ? _client.BuildConnectionString(SelectedDriver.Id, details with { Password = password })
            : ConnectionString;
    }

    public IReadOnlyList<DatabaseDriverOptionViewModel> DriverOptions { get; }

    public ObservableCollection<DatabaseTableItemViewModel> Tables { get; } = [];

    /// <summary>Lets tests and restore await the initial automatic connection.</summary>
    public Task Initialization { get; }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand RunQueryCommand { get; }

    public DatabaseWorkspaceMode SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(ShowData));
                OnPropertyChanged(nameof(ShowStructure));
                OnPropertyChanged(nameof(ShowIndexes));
            }
        }
    }

    public bool ShowData => SelectedMode == DatabaseWorkspaceMode.Data;

    public bool ShowStructure => SelectedMode == DatabaseWorkspaceMode.Structure;

    public bool ShowIndexes => SelectedMode == DatabaseWorkspaceMode.Indexes;

    public DatabaseTableItemViewModel? SelectedObject => _selectedObject;

    public bool HasSelectedObject => _selectedObject is not null;

    public string SelectedObjectName => _selectedObject?.Name ?? "Query results";

    public string ObjectPickerLabel => _selectedObject?.Name ?? "Objects";

    public IReadOnlyList<DatabaseStructureColumnViewModel> StructureColumns
    {
        get => _structureColumns;
        private set => SetProperty(ref _structureColumns, value);
    }

    public IReadOnlyList<DatabaseIndexViewModel> Indexes
    {
        get => _indexes;
        private set => SetProperty(ref _indexes, value);
    }

    public IReadOnlyList<DatabaseFilterColumnViewModel> FilterColumns
    {
        get => _filterColumns;
        private set => SetProperty(ref _filterColumns, value);
    }

    public IReadOnlyList<DatabaseFilterOperatorViewModel> FilterOperators => _filterOperators;

    public DatabaseFilterColumnViewModel? FilterColumn
    {
        get => _filterColumn;
        set
        {
            if (SetProperty(ref _filterColumn, value))
            {
                RefreshFilterOperators();
            }
        }
    }

    public DatabaseFilterOperatorViewModel? FilterOperator
    {
        get => _filterOperator;
        set
        {
            if (SetProperty(ref _filterOperator, value))
            {
                OnPropertyChanged(nameof(FilterNeedsValue));
            }
        }
    }

    public string FilterValue
    {
        get => _filterValue;
        set => SetProperty(ref _filterValue, value ?? string.Empty);
    }

    public bool FilterNeedsValue => FilterOperator?.Operator is not
        (DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull);

    public bool CanEditRows => _forcedReadOnlyReason is null
        && _selectedObjectDetails?.CanEdit == true;

    public bool CanMutateRows => CanEditRows && !IsBusy;

    public bool CanDeleteSelectedRow => CanMutateRows && SelectedRow is not null;

    public bool CanDuplicateSelectedRow => CanMutateRows && SelectedRow?.IsValid == true;

    public bool CanCopySelectedRowAsInsert => _selectedObjectDetails?.Object.Kind
        == DatabaseTableKind.Table
        && SelectedRow?.IsValid == true;

    public bool CanSetSelectedCellNull => CanMutateRows
        && SelectedRow?.Cells.Any(cell => cell.CanSetNull) == true;

    public bool CanSetSelectedCellDefault => CanMutateRows
        && SelectedRow is { IsNew: true } row
        && row.Cells.Any(cell => cell.CanSetDefault);

    public bool CanChangeSelectedObject => !HasPendingChanges && !IsBusy;

    public bool CanChangeConnection => CanChangeSelectedObject;

    public bool CanFilterTable => CanBrowseCurrentResults
        && FilterColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    public bool CanRefreshTable => CanBrowseCurrentResults
        && !HasPendingChanges
        && !IsBusy;

    public bool CanSortTable => CanBrowseCurrentResults
        && ResultColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    public bool CanChangePageLimit => CanBrowseCurrentResults
        && ResultColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    private bool CanBrowseCurrentResults =>
        _resultSource == DatabaseResultSource.StructuredTable
        || (_resultSource == DatabaseResultSource.RawQuery && _rawQueryCanBrowse);

    public bool CanGoToPreviousPage => HasPreviousPage
        && CanFilterTable
        && CanPageCurrentResults;

    public bool CanGoToNextPage => HasNextPage
        && CanFilterTable
        && CanPageCurrentResults;

    private bool CanPageCurrentResults => _resultSource == DatabaseResultSource.StructuredTable
        || (_resultSource == DatabaseResultSource.RawQuery
            && _rawQueryCanBrowse
            && _tableQuery.Sorts.Count > 0
            && HasCompleteRawResultKey);

    private bool HasCompleteRawResultKey
    {
        get
        {
            var expectedKeys = _queryProvenanceCandidate?.PrimaryKey;
            if (expectedKeys is null || expectedKeys.Count == 0)
            {
                return false;
            }

            var projectedKeys = _rawQueryColumns
                .Where(column => column.IsKey)
                .Select(column => column.BaseColumnName ?? column.Name)
                .ToHashSet(StringComparer.Ordinal);
            return projectedKeys.Count == expectedKeys.Count
                && expectedKeys.All(key => projectedKeys.Contains(key.Name));
        }
    }

    public bool CanRevertChanges => HasPendingChanges && !IsBusy;

    public string? ReadOnlyReason => _forcedReadOnlyReason
        ?? (_selectedObjectDetails is null
            ? _resultSource == DatabaseResultSource.RawQuery
                ? _rawQueryCanBrowse
                    ? "This query result does not map exactly to one editable table."
                    : "This statement result can be copied or exported, but not rerun, "
                        + "filtered, sorted, or edited safely."
                : "Run a table preview to edit rows."
            : _selectedObjectDetails.ReadOnlyReason);

    public bool HasReadOnlyReason => !CanEditRows && ReadOnlyReason is not null;

    public bool HasPreviousPage => _tableQuery.Offset > 0;

    public bool HasNextPage => _hasNextPage;

    public long TotalRows => _totalRows;

    public string TotalRowsText => TotalRows.ToString(CultureInfo.InvariantCulture);

    public string PageLimitText
    {
        get => _pageLimitText;
        set => SetProperty(ref _pageLimitText, value ?? string.Empty);
    }

    public bool HasPendingChanges => _deletedRows.Count > 0
        || ResultRows.Any(row => row.IsDirty);

    public bool CanSaveChanges => CanEditRows
        && !IsBusy
        && HasPendingChanges
        && ResultRows.All(row => row.IsValid);

    public DatabaseDriverOptionViewModel SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (HasPendingChanges)
            {
                ErrorMessage = "Save or revert the pending row changes before changing database driver.";
                OnPropertyChanged(nameof(SelectedDriver));
                return;
            }

            if (SetProperty(ref _selectedDriver, value))
            {
                SetConnected(false);
                ClearSelectedObject();
                OnPropertyChanged(nameof(RecoveryTarget));
            }
        }
    }

    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (HasPendingChanges)
            {
                ErrorMessage = "Save or revert the pending row changes before changing the connection.";
                OnPropertyChanged(nameof(ConnectionString));
                OnPropertyChanged(nameof(MaskedConnectionString));
                OnPropertyChanged(nameof(AddressBarText));
                return;
            }

            if (SetProperty(ref _connectionString, value ?? string.Empty))
            {
                SetConnected(false);
                ClearSelectedObject();
                OnPropertyChanged(nameof(MaskedConnectionString));
                OnPropertyChanged(nameof(AddressBarText));
            }
        }
    }

    private static readonly Regex PasswordAssignment = new(
        "(?<key>\\b(?:password|pwd|passphrase)\\s*=\\s*)[^;]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// What the address bar shows while not being edited: the connection
    /// string with any password value replaced by dots. The real value stays
    /// in <see cref="ConnectionString"/>.
    /// </summary>
    public string MaskedConnectionString =>
        PasswordAssignment.Replace(ConnectionString, match =>
            match.Groups["key"].Value + "••••••");

    /// <summary>The current string decomposed for the details dialog.</summary>
    public DatabaseConnectionDetails ParseConnectionDetails() =>
        _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);

    /// <summary>
    /// Applies dialog fields and probes the connection right away. Editing raw
    /// fields detaches the panel from any saved connection.
    /// </summary>
    public Task ApplyConnectionDetailsAsync(DatabaseConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection.";
            return Task.CompletedTask;
        }

        _savedConnection = null;
        _sessionPassword = null;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(SavedConnectionName));
        ConnectionString = _client.BuildConnectionString(SelectedDriver.Id, details);
        OnPropertyChanged(nameof(AddressBarText));
        OnPropertyChanged(nameof(RecoveryTarget));
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? Task.CompletedTask
            : ConnectAsync();
    }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value ?? string.Empty);
    }

    /// <summary>Filters the objects sidebar by substring, TablePlus-style.</summary>
    public string TableFilter
    {
        get => _tableFilter;
        set
        {
            if (SetProperty(ref _tableFilter, value ?? string.Empty))
            {
                RefreshTables();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
                PublishInteractionStates();
            }
        }
    }

    public bool IsConnected => _isConnected;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    public string StatusText => IsBusy
        ? "Working…"
        : IsConnected
            ? $"Connected · {Tables.Count} objects"
            : "Not connected";

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public IReadOnlyList<DatabaseResultColumnViewModel> ResultColumns
    {
        get => _resultColumns;
        private set
        {
            if (SetProperty(ref _resultColumns, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    public IReadOnlyList<DatabaseResultRowViewModel> ResultRows
    {
        get => _resultRows;
        private set
        {
            if (SetProperty(ref _resultRows, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    public bool HasResults => ResultColumns.Count > 0;

    public bool ShowEmptyHint => !HasResults;

    public DatabaseResultRowViewModel? SelectedRow => _selectedRow;

    public bool HasSelectedRow => _selectedRow is not null;

    public string SelectedRowTitle => _selectedRow is { } row ? $"Row {row.Number}" : string.Empty;

    public IReadOnlyList<DatabaseRowFieldViewModel> SelectedRowFields
    {
        get => _selectedRowFields;
        private set => SetProperty(ref _selectedRowFields, value);
    }

    /// <summary>
    /// Selects one row for the field inspector; selecting the current row again
    /// or passing null clears the inspector.
    /// </summary>
    public void SelectRow(DatabaseResultRowViewModel? row)
    {
        if (ReferenceEquals(_selectedRow, row))
        {
            row = null;
        }

        if (_selectedRow is not null)
        {
            _selectedRow.IsSelected = false;
        }

        _selectedRow = row;
        if (row is not null)
        {
            row.IsSelected = true;
        }

        RefreshSelectedRowFields();
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(SelectedRowTitle));
        PublishInteractionStates();
    }

    private void RefreshSelectedRowFields() => SelectedRowFields = _selectedRow is null
        ? []
        : ResultColumns
            .Zip(_selectedRow.Cells, (column, cell) => new DatabaseRowFieldViewModel(
                column.Name,
                column.DataTypeName,
                cell.Text,
                cell.IsNull))
            .ToArray();

    /// <summary>
    /// The durable "driverId:connection string" address, or null while the
    /// panel has no usable target. Recovery and workspace autosave persist it.
    /// </summary>
    public string? RecoveryTarget => _savedConnection is { } saved && _isPersistedConnection
        ? $"saved:{saved.Id.Value}"
        : string.IsNullOrWhiteSpace(ConnectionString)
            ? null
            : new DatabasePanelTarget(SelectedDriver.Id, ConnectionString).Serialize();

    /// <summary>The SSH connection queries tunnel through, or null for direct.</summary>
    public ConnectionId? TunnelConnectionId => _tunnelConnection?.Id;

    /// <summary>
    /// The connection pill's label: the profile this panel is bound to, or an
    /// invitation when it has nothing to connect to yet.
    /// </summary>
    public string ConnectionDisplayName => _savedConnection?.Name
        ?? (string.IsNullOrWhiteSpace(ConnectionString)
            ? "Select connection"
            : SelectedDriver.DisplayName);

    /// <summary>Something to connect to exists — a target, saved or raw.</summary>
    public bool HasConnectionTarget => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>One button reads as the action it would perform.</summary>
    public string ConnectButtonLabel => IsConnected ? "Reconnect" : "Connect";

    /// <summary>Session facts read after connecting; empty when unknown.</summary>
    public DatabaseSessionInfo SessionInfo
    {
        get => _sessionInfo;
        private set
        {
            if (SetProperty(ref _sessionInfo, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    /// <summary>Databases the connected principal may switch to.</summary>
    public IReadOnlyList<string> Databases => _databases;

    /// <summary>The selector shows only when there is a real choice to make.</summary>
    public bool HasDatabaseChoices => _databases.Count > 0;

    /// <summary>
    /// The database the session is in. Picking another rebuilds the address
    /// with it and reconnects — which is what USE means everywhere.
    /// </summary>
    public string? SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (!SetProperty(ref _selectedDatabase, value)
                || _suppressDatabaseSwitch
                || value is null)
            {
                return;
            }

            _ = SwitchDatabaseAsync(value);
        }
    }

    /// <summary>
    /// The status bar's account of the session: engine and version, transport
    /// security, route, principal, database, and selected object. Never the
    /// connection string.
    /// </summary>
    public string ConnectionSummary
    {
        get
        {
            if (!IsConnected)
            {
                return string.Empty;
            }

            var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
            var facts = new List<string>
            {
                SessionInfo.ServerVersion is { } version
                    ? $"{SelectedDriver.DisplayName} {version}"
                    : SelectedDriver.DisplayName,
            };
            if (SessionInfo.TlsProtocol is { } tls)
            {
                facts.Add(tls);
            }

            if (_tunnelConnection is { } tunnel)
            {
                facts.Add($"SSH:{tunnel.Name}");
            }

            if (details.Username is { } user)
            {
                facts.Add(user);
            }

            var database = SelectedDatabase ?? details.Database;
            if (!string.IsNullOrEmpty(database))
            {
                facts.Add(database);
            }

            if (_selectedObject is { } selected)
            {
                facts.Add(selected.Name);
            }

            return string.Join(" : ", facts);
        }
    }

    private void SetDatabases(IReadOnlyList<string> databases)
    {
        _databases = databases;
        OnPropertyChanged(nameof(Databases));
        OnPropertyChanged(nameof(HasDatabaseChoices));
    }

    /// <summary>
    /// Reads the optional session facts after a proven connection: version and
    /// TLS for the status bar, the database list for the selector. A probe the
    /// server refuses leaves the facts empty — the connection itself already
    /// succeeded.
    /// </summary>
    private async Task RefreshSessionFactsAsync(CancellationToken cancellationToken)
    {
        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        try
        {
            SessionInfo = await _client.DescribeSessionAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            SessionInfo = new DatabaseSessionInfo();
        }

        var databases = Array.Empty<string>() as IReadOnlyList<string>;
        if (SelectedDriver.CanListDatabases)
        {
            try
            {
                databases = await _client.ListDatabasesAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                databases = [];
            }
        }

        SetDatabases(databases);
        _suppressDatabaseSwitch = true;
        try
        {
            SelectedDatabase = _client
                .ParseConnectionDetails(SelectedDriver.Id, ConnectionString)
                .Database;
        }
        finally
        {
            _suppressDatabaseSwitch = false;
        }

        OnPropertyChanged(nameof(ConnectionSummary));
    }

    private async Task SwitchDatabaseAsync(string database)
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before switching databases.";
            return;
        }

        var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
        if (string.Equals(details.Database, database, StringComparison.Ordinal))
        {
            return;
        }

        // The saved profile stays bound: the switch is session state, and
        // recovery returns to the profile's own database.
        ConnectionString = _client.BuildConnectionString(
            SelectedDriver.Id,
            details with { Database = database });
        await ConnectAsync();
    }

    /// <summary>
    /// Forgets the session without touching what it pointed at: tables, the
    /// database list, and session facts clear; the bound connection stays so
    /// Connect brings it back.
    /// </summary>
    public void Disconnect()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before disconnecting.";
            return;
        }

        SetConnected(false);
        ClearSelectedObject();
        _allTables = [];
        RefreshTables();
        SetDatabases([]);
        SessionInfo = new DatabaseSessionInfo();
        ErrorMessage = null;
    }

    /// <summary>
    /// Routes queries through an SSH local port-forward over the given
    /// connection; a null or non-SSH connection means a direct connection. A
    /// connected panel re-probes through the new route immediately.
    /// </summary>
    public void SetTunnel(ConnectionProfile? connection)
    {
        var tunnel = connection?.Endpoint is ConnectionEndpoint.Ssh ? connection : null;
        if (tunnel?.Id == _tunnelConnection?.Id)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection route.";
            return;
        }

        _tunnelConnection = tunnel;
        OnPropertyChanged(nameof(TunnelConnectionId));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        SetConnected(false);
        ClearSelectedObject();
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            _ = ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before reconnecting.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            ErrorMessage = "Enter a connection string first.";
            return;
        }

        if (NeedsPasswordPrompt)
        {
            PasswordRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            var tables = await _client.ListTablesAsync(
                SelectedDriver.Id,
                await ResolveEffectiveConnectionStringAsync(cancellationToken),
                _tunnelConnection,
                cancellationToken);
            _allTables = tables
                .Select(table => new DatabaseTableItemViewModel(table))
                .ToArray();
            RefreshTables();
            SetConnected(true);
            OnPropertyChanged(nameof(RecoveryTarget));
            await RefreshSessionFactsAsync(cancellationToken);
        });
    }

    public async Task RunQueryAsync()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before running SQL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            ErrorMessage = "Enter a statement to run.";
            return;
        }

        await ExecuteQueryAsync(QueryText);
    }

    public async Task PreviewTableAsync(DatabaseTableItemViewModel table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (IsBusy)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage ??= "Save or revert the pending row changes before opening another object.";
            return;
        }

        var preview = _client.BuildTablePreviewQuery(
            SelectedDriver.Id,
            table.Descriptor.Id,
            PreviewRows);
        _selectedObject = table;
        _selectedObjectDetails = null;
        _queryProvenanceCandidate = null;
        _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
        _resultSource = DatabaseResultSource.None;
        _rawQuerySql = null;
        _rawQueryColumns = [];
        _rawQueryCanBrowse = false;
        ResultRows = [];
        ResultColumns = [];
        SelectRow(null);
        QueryText = preview;
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        PublishTableCapabilities();
        await LoadSelectedTableAsync(loadDetails: true);
    }

    public void SetMode(DatabaseWorkspaceMode mode)
    {
        SelectedMode = mode == DatabaseWorkspaceMode.Data || SelectedObject is not null
            ? mode
            : DatabaseWorkspaceMode.Data;
    }

    public async Task ApplyFilterAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        if (FilterColumn is null || FilterOperator is null)
        {
            ErrorMessage = "Choose a column and operator first.";
            return;
        }

        if (!TryParseFilterValue(
                FilterValue,
                FilterColumn.ValueKind,
                FilterOperator.Operator,
                out var value,
                out var validationError))
        {
            ErrorMessage = validationError;
            return;
        }

        var query = new DatabaseTableQuery(
            [new DatabaseFilterCondition(
                FilterColumn.Name,
                FilterOperator.Operator,
                value)],
            _tableQuery.Sorts,
            Offset: 0,
            Limit: _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    /// <summary>
    /// Operators valid for the selected cell's semantic type. NULL has its own
    /// two predicates; presenting comparisons against a fake text token would
    /// change its database meaning.
    /// </summary>
    public IReadOnlyList<DatabaseFilterOperatorViewModel> GetQuickFilterOperators(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (cell is null || cell.IsDefault)
        {
            return [];
        }

        return cell.IsNull
            ? AllFilterOperators.Where(option => option.Operator is
                DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull).ToArray()
            : FilterOperatorsFor(cell.Column.ValueKind, includeListOperators: true);
    }

    public async Task ApplyQuickFilterAsync(
        int ordinal,
        DatabaseFilterOperator filterOperator)
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        var cell = GetSelectedCell(ordinal);
        if (cell is null
            || ordinal >= ResultColumns.Count)
        {
            return;
        }

        var option = GetQuickFilterOperators(ordinal).FirstOrDefault(candidate =>
            candidate.Operator == filterOperator);
        if (option is null)
        {
            ErrorMessage = $"{filterOperator} is not available for {cell.Column.Name}.";
            return;
        }

        var filterColumn = FilterColumns.FirstOrDefault(column =>
            string.Equals(column.Name, cell.Column.Name, StringComparison.Ordinal));
        if (filterColumn is null)
        {
            ErrorMessage = $"Column '{cell.Column.Name}' is no longer available.";
            return;
        }

        object? value = filterOperator switch
        {
            DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull => null,
            DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn =>
                new object?[] { cell.RawValue },
            _ => cell.RawValue,
        };
        if (value is null
            && filterOperator is not (DatabaseFilterOperator.IsNull
                or DatabaseFilterOperator.IsNotNull))
        {
            ErrorMessage = $"{cell.Column.Name} does not contain a valid filter value.";
            return;
        }

        FilterColumn = filterColumn;
        FilterOperator = option;
        FilterValue = cell.IsNull
            ? string.Empty
            : filterOperator is DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn
                ? QuoteCsvField(cell.EditText)
                : cell.EditText;
        var query = new DatabaseTableQuery(
            [new DatabaseFilterCondition(cell.Column.Name, filterOperator, value)],
            _tableQuery.Sorts,
            Offset: 0,
            Limit: _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ClearFilterAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        FilterValue = string.Empty;
        var query = new DatabaseTableQuery([], _tableQuery.Sorts, 0, _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ToggleTableSortAsync(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!CanSortTable
            || ResultColumns.All(column =>
                !string.Equals(column.Name, columnName, StringComparison.Ordinal)))
        {
            return;
        }

        var current = _tableQuery.Sorts.Count == 1
            && string.Equals(
                _tableQuery.Sorts[0].ColumnName,
                columnName,
                StringComparison.Ordinal)
                ? _tableQuery.Sorts[0]
                : null;
        var query = _tableQuery with
        {
            Sorts = [new DatabaseSort(columnName, current is { Descending: false })],
            Offset = 0,
        };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ApplyPageLimitAsync()
    {
        if (!CanChangePageLimit)
        {
            return;
        }

        if (!int.TryParse(
                PageLimitText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var limit)
            || limit is < 1 or > MaximumPageRows)
        {
            ErrorMessage = $"Page size must be between 1 and {MaximumPageRows}.";
            return;
        }

        if (limit == _tableQuery.Limit)
        {
            PageLimitText = limit.ToString(CultureInfo.InvariantCulture);
            ErrorMessage = null;
            return;
        }

        var query = _tableQuery with { Offset = 0, Limit = limit };
        await LoadResultQueryAsync(query, loadDetails: false);
        if (_tableQuery.Limit != limit)
        {
            PageLimitText = _tableQuery.Limit.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task NextPageAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !HasNextPage)
        {
            return;
        }

        var query = _tableQuery with { Offset = _tableQuery.Offset + _tableQuery.Limit };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task PreviousPageAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !HasPreviousPage)
        {
            return;
        }

        var query = _tableQuery with
        {
            Offset = Math.Max(0, _tableQuery.Offset - _tableQuery.Limit),
        };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    /// <summary>Re-reads the current structured page or successful result query.</summary>
    public async Task RefreshTableAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !CanRefreshTable)
        {
            return;
        }

        await LoadResultQueryAsync(_tableQuery, loadDetails: true);
    }

    public void AddRow()
    {
        if (!CanMutateRows || ResultColumns.Count == 0)
        {
            return;
        }

        var row = CreateNewRow(
            ResultRows.Select(candidate => candidate.Number).DefaultIfEmpty().Max() + 1);
        ObserveRow(row);
        ResultRows = [.. ResultRows, row];
        SelectRow(row);
        PublishPendingChanges();
    }

    public void DuplicateSelectedRow()
    {
        if (!CanDuplicateSelectedRow || SelectedRow is not { } source)
        {
            return;
        }

        var number = ResultRows.Select(candidate => candidate.Number).DefaultIfEmpty().Max() + 1;
        var duplicate = source.DuplicateAsNew(number);
        ObserveRow(duplicate);
        ResultRows = [.. ResultRows, duplicate];
        SelectRow(duplicate);
        PublishPendingChanges();
    }

    /// <summary>
    /// Stages a complete CSV document only after every header and value has
    /// validated. A malformed later row therefore cannot leave earlier rows dirty.
    /// </summary>
    public bool ImportCsv(string text)
    {
        if (!CanMutateRows || ResultColumns.Count == 0)
        {
            ErrorMessage = ReadOnlyReason ?? "Rows cannot be imported right now.";
            return false;
        }

        DatabaseGridCsvDocument document;
        try
        {
            document = DatabaseGridCsv.Parse(
                text,
                Math.Min(ResultColumns.Count, DatabaseGridCsv.MaximumColumns));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        if (document.Rows.Count == 0)
        {
            ErrorMessage = "The CSV file has no data rows.";
            return false;
        }

        try
        {
            DatabaseGridCsv.ValidateStagingSize(
                document.Rows.Count,
                ResultColumns.Count);
        }
        catch (InvalidDataException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        var columnsByName = ResultColumns
            .Select((column, ordinal) => (column, ordinal))
            .ToDictionary(item => item.column.Name, item => item, StringComparer.Ordinal);
        var importedColumns = new (DatabaseResultColumnViewModel Column, int Ordinal)[
            document.Headers.Count];
        for (var index = 0; index < document.Headers.Count; index++)
        {
            var header = document.Headers[index];
            if (!columnsByName.TryGetValue(header, out var match))
            {
                ErrorMessage = $"CSV column '{DatabaseGridCsv.DescribeHeader(header)}' does not exist in this table.";
                return false;
            }

            if (match.column.Descriptor.IsReadOnly || match.column.Descriptor.IsIdentity)
            {
                ErrorMessage = $"CSV column '{DatabaseGridCsv.DescribeHeader(header)}' is owned by the database and cannot be imported.";
                return false;
            }

            importedColumns[index] = (match.column, match.ordinal);
        }

        var firstNumber = ResultRows.Select(row => row.Number).DefaultIfEmpty().Max() + 1;
        var staged = new List<DatabaseResultRowViewModel>(document.Rows.Count);
        for (var rowIndex = 0; rowIndex < document.Rows.Count; rowIndex++)
        {
            var row = CreateNewRow(firstNumber + rowIndex);
            for (var columnIndex = 0; columnIndex < importedColumns.Length; columnIndex++)
            {
                var imported = importedColumns[columnIndex];
                var cell = row.Cells[imported.Ordinal];
                if (!cell.CanSetText)
                {
                    ErrorMessage = $"CSV column '{imported.Column.Name}' cannot accept text values.";
                    return false;
                }

                var importedText = document.Rows[rowIndex][columnIndex];
                if (!TryValidateProviderTextValue(cell, importedText, out var cellProviderError))
                {
                    ErrorMessage = $"CSV row {rowIndex + 2}: {cellProviderError}";
                    return false;
                }

                cell.SetText(importedText);
            }

            if (!TryValidateProviderTextCells(
                    row.Cells.Where(cell => !cell.Column.IsReadOnly),
                    out var rowProviderError))
            {
                ErrorMessage = $"CSV row {rowIndex + 2}: {rowProviderError}";
                return false;
            }

            if (!row.IsValid)
            {
                var error = row.Cells.First(cell => !cell.IsValid).ValidationError;
                ErrorMessage = $"CSV row {rowIndex + 2}: {error}";
                return false;
            }

            staged.Add(row);
        }

        foreach (var row in staged)
        {
            ObserveRow(row);
        }

        ResultRows = [.. ResultRows, .. staged];
        SelectRow(staged[^1]);
        ErrorMessage = null;
        PublishPendingChanges();
        return true;
    }

    public void DeleteSelectedRow()
    {
        if (!CanDeleteSelectedRow || SelectedRow is not { } row)
        {
            return;
        }

        if (!row.IsNew)
        {
            _deletedRows.Add(row);
        }

        ResultRows = ResultRows.Where(candidate => !ReferenceEquals(candidate, row)).ToArray();
        SelectRow(null);
        PublishPendingChanges();
    }

    public void SetSelectedCellNull(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (CanMutateRows && cell?.CanSetNull == true)
        {
            cell.SetNull();
            RefreshSelectedRowFields();
            PublishPendingChanges();
        }
    }

    public void SetSelectedCellDefault(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (CanMutateRows
            && SelectedRow is { IsNew: true }
            && cell?.CanSetDefault == true)
        {
            cell.SetDefault();
            RefreshSelectedRowFields();
            PublishPendingChanges();
        }
    }

    public bool CanSetSelectedCellEmpty(int ordinal) => CanMutateRows
        && !string.Equals(SelectedDriver.Id, "oracle", StringComparison.Ordinal)
        && GetSelectedCell(ordinal)?.CanSetEmpty == true;

    public void SetSelectedCellEmpty(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetEmpty != true)
        {
            return;
        }

        SetSelectedCellText(ordinal, string.Empty);
    }

    public void SetSelectedCellText(int ordinal, string text)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetText != true)
        {
            return;
        }

        var normalized = text ?? string.Empty;
        if (!TryValidateProviderTextValue(cell, normalized, out var providerError))
        {
            ErrorMessage = providerError;
            return;
        }

        cell.SetText(normalized);
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    public void SetSelectedCellBinary(int ordinal, ReadOnlyMemory<byte> value)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetBinary != true)
        {
            return;
        }

        cell.SetBinary(value);
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    public void ReportInteractionError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ErrorMessage = message;
    }

    public async Task RevertChangesAsync()
    {
        if (CanRevertChanges)
        {
            await LoadResultQueryAsync(_tableQuery, loadDetails: false);
        }
    }

    public async Task SaveChangesAsync()
    {
        if (!CanSaveChanges || SelectedObject is null)
        {
            return;
        }

        if (!TryValidatePendingChangesForProvider(out var providerError))
        {
            ErrorMessage = providerError;
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            var selectedObject = SelectedObject;
            if (selectedObject is null)
            {
                return;
            }

            var inserts = ResultRows.Where(row => row.IsNew).Select(row => row.BuildInsert()).ToArray();
            var updates = ResultRows
                .Where(row => !row.IsNew && row.IsDirty)
                .Select(row => row.BuildUpdate())
                .ToArray();
            var deletes = _deletedRows.Select(row => row.BuildDelete()).ToArray();
            var result = await _client.ApplyTableChangesAsync(
                SelectedDriver.Id,
                await ResolveEffectiveConnectionStringAsync(cancellationToken),
                _tunnelConnection,
                selectedObject.Descriptor,
                new DatabaseTableChanges(inserts, updates, deletes),
                cancellationToken);
            if (result.HasConflict)
            {
                ErrorMessage = result.Message
                    ?? "The row changed in the database. Reload it before saving again.";
                return;
            }

            AcceptCommittedChanges();
            ResultSummary = $"Saved {result.TotalAffected} change(s).";
            await ReloadResultsWithinOperationAsync(cancellationToken);
        });
    }

    private bool TryValidatePendingChangesForProvider(out string? error)
    {
        foreach (var row in ResultRows.Where(row => row.IsDirty))
        {
            var cells = row.IsNew
                ? row.Cells.Where(cell => !cell.Column.IsReadOnly)
                : row.Cells.Where(cell => cell.IsDirty && !cell.Column.IsReadOnly);
            if (!TryValidateProviderTextCells(cells, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool TryValidateProviderTextCells(
        IEnumerable<DatabaseResultCellViewModel> cells,
        out string? error)
    {
        foreach (var cell in cells)
        {
            if (cell.State == DatabaseEditValueState.Value
                && cell.RawValue is string text
                && !TryValidateProviderTextValue(cell, text, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool TryValidateProviderTextValue(
        DatabaseResultCellViewModel cell,
        string text,
        out string? error)
    {
        if (text.Length == 0
            && cell.Column.ValueKind == DatabaseValueKind.Text
            && string.Equals(SelectedDriver.Id, "oracle", StringComparison.Ordinal))
        {
            error = $"Oracle stores empty text as SQL NULL. Column '{cell.Column.Name}' "
                + "must use explicit SQL NULL or a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }

    public override void Dispose()
    {
        // Both the closing tab and the disposing window sweep panels, so a
        // second call must be a no-op.
        if (!_disposed)
        {
            _disposed = true;
            _tableLoadCancellation?.Cancel();
            _tableLoadCancellation?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        base.Dispose();
    }

    private async Task ExecuteQueryAsync(string sql) =>
        await RunGuardedAsync(async cancellationToken =>
        {
            var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
            var canBrowse = IsBrowsableResultQuery(sql);
            var page = _queryProvenanceCandidate is null || !canBrowse
                ? await _client.QueryAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    MaxRows,
                    cancellationToken)
                : await _client.QueryWithProvenanceAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    MaxRows,
                    cancellationToken);
            var match = _queryProvenanceCandidate is null || !canBrowse
                ? null
                : DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
                    page,
                    _allTables.Select(table => table.Descriptor).ToArray(),
                    _queryProvenanceCandidate);
            var query = DatabaseTableQuery.FirstPage(MaxRows);
            var totalRows = canBrowse && page.Columns.Count > 0
                ? await _client.CountQueryRowsAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    page.Columns,
                    query.Filters,
                    cancellationToken)
                : page.ValueRows.Count;
            ApplyRawQueryPage(
                new DatabaseTablePage(
                    page,
                    0,
                    MaxRows,
                    page.Truncated,
                    totalRows),
                match?.Details,
                sql,
                query,
                canBrowse);
        });

    private async Task LoadResultQueryAsync(
        DatabaseTableQuery query,
        bool loadDetails)
    {
        if (_resultSource == DatabaseResultSource.RawQuery)
        {
            await LoadRawQueryAsync(query);
            return;
        }

        await LoadSelectedTableAsync(loadDetails, query);
    }

    private async Task LoadRawQueryAsync(DatabaseTableQuery requestedQuery)
    {
        if (_rawQuerySql is null
            || _rawQueryColumns.Count == 0
            || _lifetime.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _tableLoadGeneration);
        var previous = _tableLoadCancellation;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tableLoadCancellation = loadCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = loadCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var page = await ReadRawQueryPageAsync(requestedQuery, cancellationToken);
            if (generation == _tableLoadGeneration && !cancellationToken.IsCancellationRequested)
            {
                ApplyRawQueryPage(page, _selectedObjectDetails, _rawQuerySql, requestedQuery);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            if (generation == _tableLoadGeneration)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (generation == _tableLoadGeneration)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(StatusText));
                if (ReferenceEquals(_tableLoadCancellation, loadCancellation))
                {
                    _tableLoadCancellation = null;
                    loadCancellation.Dispose();
                }
            }
        }
    }

    private async Task LoadSelectedTableAsync(
        bool loadDetails,
        DatabaseTableQuery? requestedQuery = null)
    {
        var selectedObject = SelectedObject;
        if (selectedObject is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _tableLoadGeneration);
        var previous = _tableLoadCancellation;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tableLoadCancellation = loadCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = loadCancellation.Token;
        requestedQuery ??= _tableQuery;
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
            var details = loadDetails || _selectedObjectDetails is null
                ? await _client.GetObjectDetailsAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    selectedObject.Descriptor,
                    cancellationToken)
                : _selectedObjectDetails;
            var page = await _client.ReadTableAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                selectedObject.Descriptor,
                requestedQuery,
                cancellationToken);
            if (generation == _tableLoadGeneration && !cancellationToken.IsCancellationRequested)
            {
                ApplyTablePage(details, page, requestedQuery);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            if (generation == _tableLoadGeneration)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (generation == _tableLoadGeneration)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(StatusText));
                if (ReferenceEquals(_tableLoadCancellation, loadCancellation))
                {
                    _tableLoadCancellation = null;
                    loadCancellation.Dispose();
                }
            }
        }
    }

    private async Task ReloadResultsWithinOperationAsync(CancellationToken cancellationToken)
    {
        if (_resultSource == DatabaseResultSource.RawQuery)
        {
            if (_rawQuerySql is null || _rawQueryColumns.Count == 0)
            {
                return;
            }

            var rawPage = await ReadRawQueryPageAsync(_tableQuery, cancellationToken);
            ApplyRawQueryPage(rawPage, _selectedObjectDetails, _rawQuerySql, _tableQuery);
            return;
        }

        var selectedObject = SelectedObject;
        if (selectedObject is null)
        {
            return;
        }

        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        var details = _selectedObjectDetails
            ?? await _client.GetObjectDetailsAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                selectedObject.Descriptor,
                cancellationToken);
        var page = await _client.ReadTableAsync(
            SelectedDriver.Id,
            connectionString,
            _tunnelConnection,
            selectedObject.Descriptor,
            _tableQuery,
            cancellationToken);
        ApplyTablePage(details, page, _tableQuery);
    }

    private async Task<DatabaseTablePage> ReadRawQueryPageAsync(
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        var sourceSql = _rawQuerySql
            ?? throw new InvalidOperationException("The result query is no longer available.");
        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        if (query.Offset == 0 && query.Filters.Count == 0 && query.Sorts.Count == 0)
        {
            var result = await _client.QueryAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                sourceSql,
                query.Limit,
                cancellationToken);
            var totalRows = await _client.CountQueryRowsAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                sourceSql,
                _rawQueryColumns,
                query.Filters,
                cancellationToken);
            return new DatabaseTablePage(
                PreserveRawQueryColumnContext(result),
                0,
                query.Limit,
                result.Truncated,
                totalRows);
        }

        var page = await _client.ReadQueryAsync(
            SelectedDriver.Id,
            connectionString,
            _tunnelConnection,
            sourceSql,
            _rawQueryColumns,
            query,
            cancellationToken);
        return page with { Result = PreserveRawQueryColumnContext(page.Result) };
    }

    private DatabaseQueryPage PreserveRawQueryColumnContext(DatabaseQueryPage result)
    {
        if (result.Columns.Count != _rawQueryColumns.Count)
        {
            return result;
        }

        var columns = result.Columns
            .Select((column, ordinal) =>
            {
                var source = _rawQueryColumns[ordinal];
                return string.Equals(column.Name, source.Name, StringComparison.Ordinal)
                    ? source with
                    {
                        DataTypeName = column.DataTypeName,
                        ValueKind = column.ValueKind,
                        ClrTypeName = column.ClrTypeName,
                        // This SELECT was already proven as the exact table
                        // projection. Some providers describe an ordinary
                        // ORDER BY/LIMIT execution as read-only even though
                        // its catalog-backed source remains writable. Row
                        // materialization is still revalidated below, so an
                        // unsafe provider-owned value continues to fail closed.
                        IsReadOnly = source.IsReadOnly,
                    }
                    : column;
            })
            .ToArray();
        return result with { Columns = columns };
    }

    private void ApplyRawQueryPage(
        DatabaseTablePage page,
        DatabaseObjectDetails? details,
        string sourceSql,
        DatabaseTableQuery requestedQuery,
        bool? canBrowse = null)
    {
        var result = page.Result;
        var isNewSource = !string.Equals(_rawQuerySql, sourceSql, StringComparison.Ordinal);
        var isExplicitSourceExecution = canBrowse is not null;
        _rawQuerySql = sourceSql;
        if (canBrowse is not null)
        {
            _rawQueryCanBrowse = canBrowse.Value;
        }
        if (isNewSource || isExplicitSourceExecution || _rawQueryColumns.Count == 0)
        {
            _rawQueryColumns = result.Columns.ToArray();
        }

        if (result.Columns.Count == 0)
        {
            ClearSelectedObject();
            var elapsed = result.Elapsed.TotalMilliseconds
                .ToString("0", CultureInfo.InvariantCulture);
            ResultSummary = $"{result.RowsAffected} row(s) affected · {elapsed} ms";
            return;
        }

        if (details is not null)
        {
            _selectedObject = _allTables
                .FirstOrDefault(table => table.Descriptor == details.Object)
                ?? new DatabaseTableItemViewModel(details.Object);
            OnPropertyChanged(nameof(SelectedObject));
            OnPropertyChanged(nameof(HasSelectedObject));
            OnPropertyChanged(nameof(SelectedObjectName));
            OnPropertyChanged(nameof(ObjectPickerLabel));
            ApplyTablePage(
                details,
                page,
                requestedQuery,
                DatabaseResultSource.RawQuery);
            if (isNewSource || isExplicitSourceExecution)
            {
                _rawQueryColumns = ResultColumns
                    .Select(column => column.Descriptor)
                    .ToArray();
            }
            return;
        }

        _selectedObject = null;
        _selectedObjectDetails = null;
        StructureColumns = [];
        Indexes = [];
        _deletedRows.Clear();
        SelectRow(null);
        ApplyPagerState(page, requestedQuery);
        _resultSource = DatabaseResultSource.RawQuery;
        var selectedFilterColumnName = FilterColumn?.Name;
        var selectedFilterOperator = FilterOperator?.Operator;
        FilterColumns = result.Columns
            .Select((column, ordinal) => new DatabaseFilterColumnViewModel(
                new DatabaseColumnSchema(
                    column.Name,
                    ordinal,
                    column.DataTypeName,
                    column.ValueKind,
                    column.ClrTypeName,
                    column.IsNullable,
                    IsPrimaryKey: column.IsKey,
                    IsIdentity: column.IsIdentity,
                    IsReadOnly: true,
                    DefaultExpression: column.DefaultExpression)))
            .ToArray();
        FilterColumn = FilterColumns.FirstOrDefault(column =>
                string.Equals(column.Name, selectedFilterColumnName, StringComparison.Ordinal))
            ?? FilterColumns.FirstOrDefault();
        FilterOperator = FilterOperators.FirstOrDefault(option =>
                option.Operator == selectedFilterOperator)
            ?? FilterOperators.FirstOrDefault();

        var widths = ComputeColumnWidths(result);
        var nextColumns = result.Columns
            .Select((column, index) => new DatabaseResultColumnViewModel(
                column,
                widths[index],
                canEdit: false,
                SortDirectionFor(column.Name, requestedQuery)))
            .ToArray();
        // Detach realized rows only when the cell-template shape changes.
        // Same-shape filters/sorts can update their header state in place;
        // clearing and rebuilding a live DataGrid for every page causes
        // re-entrant layout with synchronously completing file providers.
        if (!HasCompatibleResultColumnShape(nextColumns))
        {
            ResultRows = [];
        }

        ResultColumns = nextColumns;
        ResultRows = result.ValueRows
            .Select((row, index) => new DatabaseResultRowViewModel(
                page.Offset + index + 1,
                row,
                result.Columns,
                widths,
                canEdit: false))
            .ToArray();
        var elapsedText = result.Elapsed.TotalMilliseconds
            .ToString("0", CultureInfo.InvariantCulture);
        ResultSummary = result.Truncated
            ? $"First {result.ValueRows.Count} rows (truncated) · {elapsedText} ms"
            : $"{result.ValueRows.Count} rows · {elapsedText} ms";
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private void ApplyTablePage(
        DatabaseObjectDetails details,
        DatabaseTablePage page,
        DatabaseTableQuery requestedQuery,
        DatabaseResultSource resultSource = DatabaseResultSource.StructuredTable)
    {
        var result = page.Result;
        var valueRows = result.ValueRows;
        var normalizedColumns = details.Columns.Select(column =>
        {
            var materializedOrdinal = FindColumnOrdinal(result.Columns, column.Name);
            var materialized = materializedOrdinal >= 0
                ? result.Columns[materializedOrdinal]
                : null;
            if (materializedOrdinal >= 0 && HasDisplayOnlyValue(valueRows, materializedOrdinal))
            {
                // A provider-owned value that could not be detached safely is a
                // stronger signal than catalog metadata. Never turn its bounded
                // display text back into an editable/provider-bound value.
                return column with
                {
                    ValueKind = DatabaseValueKind.Other,
                    IsReadOnly = true,
                };
            }

            return column.ValueKind == DatabaseValueKind.Other
                && materialized is { ValueKind: not DatabaseValueKind.Other }
                    ? column with
                    {
                        ValueKind = materialized.ValueKind,
                        ClrTypeName = materialized.ClrTypeName ?? column.ClrTypeName,
                    }
                    : column;
        }).ToArray();
        var hasDisplayOnlyKey = normalizedColumns.Any(column =>
            column.IsPrimaryKey && column.ValueKind == DatabaseValueKind.Other);
        var disableForDisplayOnlyKey = details.CanEdit && hasDisplayOnlyKey;
        details = details with
        {
            Columns = normalizedColumns,
            CanEdit = details.CanEdit && !hasDisplayOnlyKey,
            ReadOnlyReason = disableForDisplayOnlyKey
                ? "This primary-key value cannot be edited safely."
                : details.ReadOnlyReason,
        };
        var effectiveColumns = result.Columns.Select((column, ordinal) =>
        {
            var metadata = details.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
            if (HasDisplayOnlyValue(valueRows, ordinal))
            {
                return column with
                {
                    ValueKind = DatabaseValueKind.Other,
                    IsReadOnly = true,
                };
            }

            return metadata is null
                ? column
                : column with
                {
                    ValueKind = metadata.ValueKind == DatabaseValueKind.Other
                        ? column.ValueKind
                        : metadata.ValueKind,
                    ClrTypeName = column.ClrTypeName ?? metadata.ClrTypeName,
                    IsNullable = metadata.IsNullable,
                    IsKey = metadata.IsPrimaryKey,
                    IsIdentity = metadata.IsIdentity,
                    IsReadOnly = column.IsReadOnly || !metadata.CanEdit,
                    BaseColumnName = metadata.Name,
                    DefaultExpression = metadata.DefaultExpression,
                };
        }).ToArray();
        result = result with { Columns = effectiveColumns };
        _selectedObjectDetails = details;
        _queryProvenanceCandidate = details;
        StructureColumns = details.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => new DatabaseStructureColumnViewModel(column))
            .ToArray();
        Indexes = details.Indexes.Select(index => new DatabaseIndexViewModel(index)).ToArray();
        var selectedFilterColumnName = FilterColumn?.Name;
        var selectedFilterOperator = FilterOperator?.Operator;
        FilterColumns = details.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => new DatabaseFilterColumnViewModel(column))
            .ToArray();
        FilterColumn = FilterColumns.FirstOrDefault(column =>
                string.Equals(column.Name, selectedFilterColumnName, StringComparison.Ordinal))
            ?? FilterColumns.FirstOrDefault();
        FilterOperator = FilterOperators.FirstOrDefault(option =>
                option.Operator == selectedFilterOperator)
            ?? FilterOperators.FirstOrDefault();
        ApplyPagerState(page, requestedQuery);
        _resultSource = resultSource;
        if (resultSource == DatabaseResultSource.StructuredTable)
        {
            _rawQuerySql = null;
            _rawQueryColumns = [];
            _rawQueryCanBrowse = false;
        }
        _deletedRows.Clear();
        SelectRow(null);

        var widths = ComputeColumnWidths(result);
        var nextColumns = result.Columns
            .Select((column, index) => new DatabaseResultColumnViewModel(
                column,
                widths[index],
                CanEditRows,
                SortDirectionFor(column.Name, requestedQuery)))
            .ToArray();
        if (!HasCompatibleResultColumnShape(nextColumns))
        {
            ResultRows = [];
        }

        ResultColumns = nextColumns;
        var rows = valueRows
            .Select((values, index) => new DatabaseResultRowViewModel(
                page.Offset + index + 1,
                values,
                result.Columns,
                widths,
                CanEditRows))
            .ToArray();
        foreach (var row in rows)
        {
            ObserveRow(row);
        }

        ResultRows = rows;
        var elapsed = result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        ResultSummary = result.Truncated && !page.HasMore
            ? $"First {valueRows.Count} rows · paging requires a primary key · {elapsed} ms"
            : valueRows.Count == 0
                ? $"No rows · {elapsed} ms"
                : $"Rows {page.Offset + 1}–{page.Offset + valueRows.Count} · {elapsed} ms";
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private bool HasCompatibleResultColumnShape(
        IReadOnlyList<DatabaseResultColumnViewModel> replacements)
    {
        if (ResultColumns.Count != replacements.Count)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < replacements.Count; ordinal++)
        {
            var current = ResultColumns[ordinal];
            var replacement = replacements[ordinal];
            if (!string.Equals(current.Name, replacement.Name, StringComparison.Ordinal)
                || current.ValueKind != replacement.ValueKind
                || current.IsEditable != replacement.IsEditable)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyPagerState(DatabaseTablePage page, DatabaseTableQuery requestedQuery)
    {
        _hasNextPage = page.HasMore;
        _totalRows = page.TotalRows;
        _tableQuery = requestedQuery with { Offset = page.Offset, Limit = page.Limit };
        PageLimitText = page.Limit.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(TotalRows));
        OnPropertyChanged(nameof(TotalRowsText));
    }

    private static int FindColumnOrdinal(
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        string name)
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (string.Equals(columns[ordinal].Name, name, StringComparison.Ordinal))
            {
                return ordinal;
            }
        }

        return -1;
    }

    private static bool? SortDirectionFor(
        string columnName,
        DatabaseTableQuery query)
    {
        var sort = query.Sorts.Count == 1
            && string.Equals(
                query.Sorts[0].ColumnName,
                columnName,
                StringComparison.Ordinal)
                ? query.Sorts[0]
                : null;
        return sort?.Descending;
    }

    private static bool HasDisplayOnlyValue(
        IReadOnlyList<IReadOnlyList<DatabaseValue>> rows,
        int ordinal) =>
        rows.Any(row =>
            ordinal < row.Count
            && !row[ordinal].IsNull
            && row[ordinal].Kind == DatabaseValueKind.Other);

    private void ClearSelectedObject()
    {
        Interlocked.Increment(ref _tableLoadGeneration);
        var activeLoad = _tableLoadCancellation;
        _tableLoadCancellation = null;
        activeLoad?.Cancel();
        activeLoad?.Dispose();
        if (activeLoad is not null)
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
        _selectedObject = null;
        _selectedObjectDetails = null;
        _queryProvenanceCandidate = null;
        _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
        _resultSource = DatabaseResultSource.None;
        _rawQuerySql = null;
        _rawQueryColumns = [];
        _rawQueryCanBrowse = false;
        _hasNextPage = false;
        _totalRows = 0;
        PageLimitText = PreviewRows.ToString(CultureInfo.InvariantCulture);
        _deletedRows.Clear();
        StructureColumns = [];
        Indexes = [];
        FilterColumns = [];
        FilterColumn = null;
        ResultRows = [];
        ResultColumns = [];
        SelectRow(null);
        ResultSummary = string.Empty;
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(TotalRows));
        OnPropertyChanged(nameof(TotalRowsText));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private void ObserveRow(DatabaseResultRowViewModel row) =>
        row.DirtyStateChanged += OnRowDirtyStateChanged;

    private DatabaseResultRowViewModel CreateNewRow(int number)
    {
        var columns = ResultColumns.Select(column => column.Descriptor).ToArray();
        var values = columns.Select(column => new DatabaseValue(
            null,
            column.ValueKind,
            "DEFAULT")).ToArray();
        var widths = ResultColumns.Select(column => column.Width).ToArray();
        return new DatabaseResultRowViewModel(
            number,
            values,
            columns,
            widths,
            canEdit: true,
            isNew: true);
    }

    private DatabaseResultCellViewModel? GetSelectedCell(int ordinal) =>
        SelectedRow is { } row && ordinal >= 0 && ordinal < row.Cells.Count
            ? row.Cells[ordinal]
            : null;

    private void OnRowDirtyStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    private void PublishTableCapabilities()
    {
        OnPropertyChanged(nameof(CanEditRows));
        OnPropertyChanged(nameof(CanMutateRows));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        OnPropertyChanged(nameof(CanDuplicateSelectedRow));
        OnPropertyChanged(nameof(CanCopySelectedRowAsInsert));
        OnPropertyChanged(nameof(CanSetSelectedCellNull));
        OnPropertyChanged(nameof(CanSetSelectedCellDefault));
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(HasReadOnlyReason));
        OnPropertyChanged(nameof(CanSaveChanges));
        PublishInteractionStates();
    }

    private void PublishPendingChanges()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanSaveChanges));
        RaiseCommandStates();
        PublishInteractionStates();
    }

    private void PublishInteractionStates()
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(CanMutateRows));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        OnPropertyChanged(nameof(CanDuplicateSelectedRow));
        OnPropertyChanged(nameof(CanCopySelectedRowAsInsert));
        OnPropertyChanged(nameof(CanSetSelectedCellNull));
        OnPropertyChanged(nameof(CanSetSelectedCellDefault));
        OnPropertyChanged(nameof(CanChangeSelectedObject));
        OnPropertyChanged(nameof(CanChangeConnection));
        OnPropertyChanged(nameof(CanFilterTable));
        OnPropertyChanged(nameof(CanRefreshTable));
        OnPropertyChanged(nameof(CanSortTable));
        OnPropertyChanged(nameof(CanChangePageLimit));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(CanRevertChanges));
        OnPropertyChanged(nameof(CanSaveChanges));
    }

    private void RefreshFilterOperators()
    {
        _filterOperators = FilterOperatorsFor(
            FilterColumn?.ValueKind,
            includeListOperators: true);
        OnPropertyChanged(nameof(FilterOperators));
        if (FilterOperator is null
            || !_filterOperators.Any(option => option.Operator == FilterOperator.Operator))
        {
            FilterOperator = _filterOperators[0];
        }
    }

    private static IReadOnlyList<DatabaseFilterOperatorViewModel> FilterOperatorsFor(
        DatabaseValueKind? kind,
        bool includeListOperators)
    {
        var supportsValueFilters = kind is not (DatabaseValueKind.Other
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Collection
            or DatabaseValueKind.Json
            or DatabaseValueKind.Network);
        var supportsTextMatching = kind is DatabaseValueKind.Text
            && supportsValueFilters;
        var supportsOrdering = kind is DatabaseValueKind.SignedInteger
            or DatabaseValueKind.Text
            or DatabaseValueKind.UnsignedInteger
            or DatabaseValueKind.Decimal
            or DatabaseValueKind.FloatingPoint
            or DatabaseValueKind.Date
            or DatabaseValueKind.Time
            or DatabaseValueKind.Timestamp
            or DatabaseValueKind.TimestampWithZone
            or DatabaseValueKind.Duration;
        return AllFilterOperators.Where(option => option.Operator switch
        {
            DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull => true,
            _ when !supportsValueFilters => false,
            DatabaseFilterOperator.Contains
                or DatabaseFilterOperator.NotContains
                or DatabaseFilterOperator.StartsWith
                or DatabaseFilterOperator.EndsWith => supportsTextMatching,
            DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn => includeListOperators,
            DatabaseFilterOperator.LessThan
                or DatabaseFilterOperator.LessThanOrEqual
                or DatabaseFilterOperator.GreaterThan
                or DatabaseFilterOperator.GreaterThanOrEqual => supportsOrdering,
            _ => true,
        }).ToArray();
    }

    private static bool TryParseFilterValue(
        string text,
        DatabaseValueKind kind,
        DatabaseFilterOperator filterOperator,
        out object? value,
        out string? error)
    {
        if (filterOperator is DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull)
        {
            value = null;
            error = null;
            return true;
        }

        if (filterOperator is not (DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn))
        {
            return DatabaseResultCellViewModel.TryParse(text, kind, out value, out error);
        }

        if (text.Length > MaximumFilterListCharacters)
        {
            value = null;
            error = "Filter lists are limited to 64 KiB of text.";
            return false;
        }

        var rows = DelimitedText.Parse(text, ',', maximumRows: 2);
        if (rows.Count != 1 || rows[0].Count is 0 or > MaximumFilterListValues)
        {
            value = null;
            error = $"IN filters require between 1 and {MaximumFilterListValues} comma-separated values.";
            return false;
        }

        var parsed = new object?[rows[0].Count];
        for (var index = 0; index < rows[0].Count; index++)
        {
            var token = kind == DatabaseValueKind.Text
                ? rows[0][index]
                : rows[0][index].Trim();
            if (!DatabaseResultCellViewModel.TryParse(token, kind, out parsed[index], out error))
            {
                value = null;
                return false;
            }
        }

        value = parsed;
        error = null;
        return true;
    }

    private void AcceptCommittedChanges()
    {
        _deletedRows.Clear();
        foreach (var row in ResultRows)
        {
            row.AcceptChanges();
        }

        PublishPendingChanges();
    }

    private bool CanDiscardCurrentPage()
    {
        if (!HasPendingChanges)
        {
            return true;
        }

        ErrorMessage = "Save or revert the pending row changes first.";
        return false;
    }

    private static bool IsRecoverableDatabaseException(Exception exception) => exception
        is System.Data.Common.DbException
        or InvalidOperationException
        or NotSupportedException
        or ArgumentException
        or TimeoutException
        or IOException;

    private async Task RunGuardedAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy || _lifetime.IsCancellationRequested)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string BuildCellValue(DatabaseResultRowViewModel row, int ordinal)
    {
        ValidateRowWidth(row);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, row.Cells.Count);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCellText(writer, row.Cells[ordinal]));
    }

    /// <summary>Every value in one column on the current page, one per line.</summary>
    public string BuildColumnValues(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ResultColumns.Count);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteColumnValues(writer, ResultRows, ordinal));
    }

    /// <summary>One selected row as spreadsheet-friendly tab-separated text.</summary>
    public string BuildRowTsv(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteRowTsv(writer, row));
    }

    public string BuildCurrentPageTsv()
    {
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCurrentPageTsv(writer, ResultColumns, ResultRows));
    }

    /// <summary>One row as a JSON object, numbers and booleans unquoted.</summary>
    public string BuildRowJson(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteJsonRow(writer, ResultColumns, row));
    }

    public string BuildCurrentPageJson() => DatabaseGridExport.BuildClipboardText(writer =>
        DatabaseGridExport.WriteCurrentPageJson(writer, ResultColumns, ResultRows));

    /// <summary>One row as a two-line CSV: header and RFC-quoted values.</summary>
    public string BuildRowCsv(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCsv(writer, ResultColumns, [row]));
    }

    public string BuildCurrentPageCsv() => DatabaseGridExport.BuildClipboardText(writer =>
        DatabaseGridExport.WriteCsv(writer, ResultColumns, ResultRows));

    /// <summary>One row as an executable INSERT for the active database driver.</summary>
    internal string BuildRowSqlInsert(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        var details = _selectedObjectDetails
            ?? throw new InvalidOperationException(
                "INSERT copy is only available for a physical table result.");
        if (details.Object.Kind != DatabaseTableKind.Table)
        {
            throw new InvalidOperationException(
                "INSERT copy is only available for a physical table result.");
        }

        var statement = _client.BuildInsertStatement(
            SelectedDriver.Id,
            details,
            row.BuildInsert());
        return DatabaseGridExport.BuildClipboardText(writer => writer.Write(statement));
    }

    internal string BuildCurrentPageSql()
    {
        var table = RequireSqlExportTable();
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCurrentPageSql(
                writer,
                table,
                ResultColumns,
                ResultRows));
    }

    /// <summary>Streams the current page as RFC 4180-style CSV without a page-sized string.</summary>
    public void WriteCurrentPageCsv(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCsv(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams UTF-8 CSV directly to a writable stream and leaves it open.</summary>
    public void WriteCurrentPageCsv(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageCsv(writer);
        writer.Flush();
    }

    /// <summary>Writes one complete JSON array to an existing UTF-8 JSON writer.</summary>
    public void WriteCurrentPageJson(Utf8JsonWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageJson(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams the current page as indented JSON to an existing text writer.</summary>
    public void WriteCurrentPageJson(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageJson(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams the current page as indented UTF-8 JSON and leaves the stream open.</summary>
    public void WriteCurrentPageJson(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageJson(writer);
        writer.Flush();
    }

    /// <summary>Streams ANSI INSERT statements without a page-sized string.</summary>
    internal void WriteCurrentPageSql(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageSql(
            destination,
            RequireSqlExportTable(),
            ResultColumns,
            ResultRows);
    }

    /// <summary>Streams UTF-8 ANSI INSERT statements and leaves the stream open.</summary>
    internal void WriteCurrentPageSql(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageSql(writer);
        writer.Flush();
    }

    /// <summary>
    /// Streams a snapshot of the current page off the UI thread while the panel
    /// is interaction-locked. Cell values are detached before they reach this
    /// layer, and the busy gate prevents an editor from changing their state
    /// while the snapshot is being serialized.
    /// </summary>
    internal async Task WriteCurrentPageExportAsync(
        Stream destination,
        DatabaseGridExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The export destination is not writable.", nameof(destination));
        }

        if (IsBusy)
        {
            throw new InvalidOperationException("Another database operation is already running.");
        }

        if (!HasResults)
        {
            throw new InvalidOperationException("There is no database page to export.");
        }

        var columns = ResultColumns.ToArray();
        var rows = ResultRows.ToArray();
        var table = format == DatabaseGridExportFormat.Sql
            ? RequireSqlExportTable()
            : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);

        IsBusy = true;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            await Task.Run(
                () =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    switch (format)
                    {
                        case DatabaseGridExportFormat.Csv:
                            using (var writer = new StreamWriter(
                                       destination,
                                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                       bufferSize: 16 * 1024,
                                       leaveOpen: true))
                            {
                                DatabaseGridExport.WriteCsv(writer, columns, rows);
                                writer.Flush();
                            }

                            break;
                        case DatabaseGridExportFormat.Json:
                            using (var writer = new Utf8JsonWriter(
                                       destination,
                                       new JsonWriterOptions { Indented = true }))
                            {
                                DatabaseGridExport.WriteCurrentPageJson(writer, columns, rows);
                                writer.Flush();
                            }

                            break;
                        case DatabaseGridExportFormat.Sql:
                            using (var writer = new StreamWriter(
                                       destination,
                                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                       bufferSize: 16 * 1024,
                                       leaveOpen: true))
                            {
                                DatabaseGridExport.WriteCurrentPageSql(
                                    writer,
                                    table!,
                                    columns,
                                    rows);
                                writer.Flush();
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(format));
                    }

                    linked.Token.ThrowIfCancellationRequested();
                },
                linked.Token);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private DatabaseObjectId RequireSqlExportTable() =>
        _selectedObject?.Descriptor.Id
        ?? throw new InvalidOperationException(
            "SQL INSERT export is only available for a table preview.");

    private void ValidateRowWidth(DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Cells.Count != ResultColumns.Count)
        {
            throw new ArgumentException(
                "The row does not match the current result columns.",
                nameof(row));
        }
    }

    private static string QuoteCsvField(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsBrowsableResultQuery(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        const string keyword = "SELECT";
        if (!statement.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (statement.Length == keyword.Length
                || !(char.IsLetterOrDigit(statement[keyword.Length])
                    || statement[keyword.Length] == '_'))
            && HasNoAdditionalStatement(statement[keyword.Length..]);
    }

    private static bool HasNoAdditionalStatement(ReadOnlySpan<char> sql)
    {
        var parenthesisDepth = 0;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current is '\'' or '"' or '`' or '[')
            {
                if (!SkipQuotedSql(sql, ref index, current))
                {
                    return false;
                }

                continue;
            }

            if (IsDashCommentStart(sql, index))
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                index += end + 3;
                continue;
            }

            if (current == '(')
            {
                parenthesisDepth++;
                continue;
            }

            if (current == ')')
            {
                parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                continue;
            }

            if (parenthesisDepth != 0)
            {
                continue;
            }

            if (current == ';')
            {
                // Keep the source suitable for a generated outer SELECT. A
                // comment after the terminator would either leave the
                // semicolon inside the derived table or swallow its closing
                // syntax, so this deliberately fails closed.
                return sql[(index + 1)..].Trim().IsEmpty;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                while (index + 1 < sql.Length
                       && (char.IsLetterOrDigit(sql[index + 1]) || sql[index + 1] == '_'))
                {
                    index++;
                }

                if (IsDataChangingKeyword(sql[start..(index + 1)]))
                {
                    return false;
                }
            }
        }

        return parenthesisDepth == 0;
    }

    private static bool SkipQuotedSql(
        ReadOnlySpan<char> sql,
        ref int index,
        char opener)
    {
        var closer = opener == '[' ? ']' : opener;
        for (index++; index < sql.Length; index++)
        {
            if (sql[index] != closer)
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closer)
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsDataChangingKeyword(ReadOnlySpan<char> token) =>
        token.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
        || token.Equals("INTO", StringComparison.OrdinalIgnoreCase)
        || token.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("MERGE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DROP", StringComparison.OrdinalIgnoreCase)
        || token.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
        || token.Equals("REVOKE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("CALL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("COPY", StringComparison.OrdinalIgnoreCase)
        || token.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DETACH", StringComparison.OrdinalIgnoreCase)
        || token.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase)
        || token.Equals("VACUUM", StringComparison.OrdinalIgnoreCase);

    private static bool IsDashCommentStart(ReadOnlySpan<char> sql, int index) =>
        index + 1 < sql.Length
        && sql[index] == '-'
        && sql[index + 1] == '-'
        && (index + 2 == sql.Length
            || char.IsWhiteSpace(sql[index + 2])
            || char.IsControl(sql[index + 2]));

    private void RefreshTables()
    {
        Tables.Clear();
        foreach (var table in _allTables)
        {
            if (string.IsNullOrWhiteSpace(_tableFilter)
                || table.Name.Contains(_tableFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Tables.Add(table);
            }
        }

        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Content-fitted column widths, TablePlus-style: wide enough for the
    /// header and the widest visible value, clamped so one long cell cannot
    /// push every other column off screen.
    /// </summary>
    private static double[] ComputeColumnWidths(DatabaseQueryPage page)
    {
        const double CharacterWidth = 6.6;
        const double CellPadding = 22;
        var widths = new double[page.Columns.Count];
        for (var index = 0; index < page.Columns.Count; index++)
        {
            var longest = page.Columns[index].Name.Length;
            foreach (var row in page.ValueRows)
            {
                longest = Math.Max(longest, row[index].DisplayText.Length);
            }

            widths[index] = Math.Clamp(
                CellPadding + (CharacterWidth * longest),
                76,
                340);
        }

        return widths;
    }

    private void SetConnected(bool value)
    {
        if (_isConnected == value)
        {
            return;
        }

        _isConnected = value;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectButtonLabel));
        OnPropertyChanged(nameof(ConnectionSummary));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (ConnectCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
        (RunQueryCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
    }
}
