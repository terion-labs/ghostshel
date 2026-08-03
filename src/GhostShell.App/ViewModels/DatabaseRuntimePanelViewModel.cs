using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class DatabaseDriverOptionViewModel(DatabaseDriverDescriptor descriptor)
{
    public string Id { get; } = descriptor.Id;

    public string DisplayName { get; } = descriptor.DisplayName;

    public string ConnectionStringHint { get; } = descriptor.ConnectionStringHint;

    public bool IsFileBased { get; } = descriptor.IsFileBased;
}

public sealed class DatabaseTableItemViewModel(DatabaseTableDescriptor table)
{
    public string Name { get; } = table.Name;

    public bool IsView { get; } = table.Kind == DatabaseTableKind.View;

    public string KindLabel { get; } = table.Kind == DatabaseTableKind.View ? "View" : "Table";
}

public sealed class DatabaseResultCellViewModel(string? text, double width)
{
    public bool IsNull { get; } = text is null;

    public string Text { get; } = text ?? "NULL";

    /// <summary>The owning column's width, so header and cells stay aligned.</summary>
    public double Width { get; } = width;
}

public sealed class DatabaseResultRowViewModel : ObservableObject
{
    private bool _isSelected;

    public DatabaseResultRowViewModel(
        int number,
        IReadOnlyList<string?> cells,
        IReadOnlyList<double> columnWidths)
    {
        Number = number;
        Cells = cells
            .Select((cell, index) => new DatabaseResultCellViewModel(
                cell,
                index < columnWidths.Count ? columnWidths[index] : 164))
            .ToArray();
    }

    /// <summary>The 1-based position inside the current result page.</summary>
    public int Number { get; }

    public bool IsEven => Number % 2 == 0;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public IReadOnlyList<DatabaseResultCellViewModel> Cells { get; }
}

/// <summary>One field of the selected row, presented in the inspector column.</summary>
public sealed record DatabaseRowFieldViewModel(
    string Name,
    string DataTypeName,
    string Text,
    bool IsNull);

public sealed class DatabaseResultColumnViewModel(
    DatabaseColumnDescriptor column,
    double width)
{
    public string Name { get; } = column.Name;

    public string DataTypeName { get; } = column.DataTypeName;

    public double Width { get; } = width;
}

/// <summary>
/// A generic multi-driver database viewer: pick a driver, connect with an
/// ADO.NET connection string, browse tables, and run bounded queries. All
/// engine specifics stay behind <see cref="IDatabasePanelClient"/>; the panel
/// holds no open connection between operations.
/// </summary>
public sealed class DatabaseRuntimePanelViewModel : RuntimePanelViewModel
{
    /// <summary>Result sets are display pages, not exports; the cap keeps the grid honest.</summary>
    public const int MaxRows = 500;

    private const int PreviewRows = 200;

    private readonly IDatabasePanelClient _client;
    private readonly Func<SecretRef, CancellationToken, Task<string?>>? _passwordResolver;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private ConnectionProfile? _tunnelConnection;
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
    private string? _lastPreviewedTable;
    private DatabaseResultRowViewModel? _selectedRow;
    private IReadOnlyList<DatabaseRowFieldViewModel> _selectedRowFields = [];

    public DatabaseRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IDatabasePanelClient client,
        string? driverId = null,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        Func<SecretRef, CancellationToken, Task<string?>>? passwordResolver = null)
        : base(id, PanelKind.DatabaseViewer, title, "Database")
    {
        _tunnelConnection = tunnelConnection?.Endpoint is ConnectionEndpoint.Ssh
            ? tunnelConnection
            : null;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _passwordResolver = passwordResolver;
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
        ConnectCommand = new AsyncActionCommand(ConnectAsync, () => !IsBusy);
        RunQueryCommand = new AsyncActionCommand(RunQueryAsync, () => !IsBusy && IsConnected);
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
    /// Binds this panel to a saved connection: driver and address become the
    /// profile's, the address bar shows its name, and connecting resolves the
    /// stored password — or asks for one. A session password supplied by the
    /// save flow avoids re-asking for what the user just typed.
    /// </summary>
    public void ApplySavedConnection(
        DatabaseConnectionProfile profile,
        string? sessionPassword = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _savedConnection = profile;
        _sessionPassword = string.IsNullOrEmpty(sessionPassword) ? null : sessionPassword;
        var driver = DriverOptions.FirstOrDefault(option =>
            string.Equals(option.Id, profile.DriverId, StringComparison.Ordinal));
        if (driver is not null)
        {
            _selectedDriver = driver;
            OnPropertyChanged(nameof(SelectedDriver));
        }

        ConnectionString = profile.ConnectionString;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(SavedConnectionName));
        OnPropertyChanged(nameof(AddressBarText));
        OnPropertyChanged(nameof(RecoveryTarget));
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

    public ICommand RunQueryCommand { get; }

    public DatabaseDriverOptionViewModel SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedDriver, value))
            {
                SetConnected(false);
                OnPropertyChanged(nameof(RecoveryTarget));
            }
        }
    }

    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (SetProperty(ref _connectionString, value ?? string.Empty))
            {
                SetConnected(false);
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
        private set => SetProperty(ref _resultColumns, value);
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

        SelectedRowFields = row is null
            ? []
            : ResultColumns
                .Zip(row.Cells, (column, cell) => new DatabaseRowFieldViewModel(
                    column.Name,
                    column.DataTypeName,
                    cell.Text,
                    cell.IsNull))
                .ToArray();
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(SelectedRowTitle));
    }

    /// <summary>
    /// The durable "driverId:connection string" address, or null while the
    /// panel has no usable target. Recovery and workspace autosave persist it.
    /// </summary>
    public string? RecoveryTarget => _savedConnection is { } saved
        ? $"saved:{saved.Id.Value}"
        : string.IsNullOrWhiteSpace(ConnectionString)
            ? null
            : new DatabasePanelTarget(SelectedDriver.Id, ConnectionString).Serialize();

    /// <summary>The SSH connection queries tunnel through, or null for direct.</summary>
    public ConnectionId? TunnelConnectionId => _tunnelConnection?.Id;

    /// <summary>The selector label, mirroring the File Viewer's connection pill.</summary>
    public string ConnectionDisplayName => _tunnelConnection?.Name ?? "Direct";

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

        _tunnelConnection = tunnel;
        OnPropertyChanged(nameof(TunnelConnectionId));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        SetConnected(false);
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            _ = ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
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
        });
    }

    public async Task RunQueryAsync()
    {
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
        var preview = _client.BuildTablePreviewQuery(
            SelectedDriver.Id,
            table.Name,
            PreviewRows);
        _lastPreviewedTable = table.Name;
        QueryText = preview;
        await ExecuteQueryAsync(preview);
    }

    public override void Dispose()
    {
        // Both the closing tab and the disposing window sweep panels, so a
        // second call must be a no-op.
        if (!_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        base.Dispose();
    }

    private async Task ExecuteQueryAsync(string sql) =>
        await RunGuardedAsync(async cancellationToken =>
        {
            var page = await _client.QueryAsync(
                SelectedDriver.Id,
                await ResolveEffectiveConnectionStringAsync(cancellationToken),
                _tunnelConnection,
                sql,
                MaxRows,
                cancellationToken);
            SelectRow(null);
            var widths = ComputeColumnWidths(page);
            ResultColumns = page.Columns
                .Select((column, index) => new DatabaseResultColumnViewModel(
                    column,
                    widths[index]))
                .ToArray();
            ResultRows = page.Rows
                .Select((row, index) => new DatabaseResultRowViewModel(
                    index + 1,
                    row,
                    widths))
                .ToArray();
            var elapsed = page.Elapsed.TotalMilliseconds
                .ToString("0", CultureInfo.InvariantCulture);
            ResultSummary = page.Columns.Count == 0
                ? $"{page.RowsAffected} row(s) affected · {elapsed} ms"
                : page.Truncated
                    ? $"First {page.Rows.Count} rows (truncated) · {elapsed} ms"
                    : $"{page.Rows.Count} rows · {elapsed} ms";
        });

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
        catch (Exception exception) when (exception
            is System.Data.Common.DbException
            or InvalidOperationException
            or ArgumentException
            or TimeoutException
            or IOException)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>One row as a JSON object, numbers and booleans unquoted.</summary>
    public string BuildRowJson(DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var fields = new Dictionary<string, object?>();
        foreach (var (column, cell) in ResultColumns.Zip(row.Cells))
        {
            fields[column.Name] = cell.IsNull
                ? null
                : CoerceTypedValue(column.DataTypeName, cell.Text);
        }

        return System.Text.Json.JsonSerializer.Serialize(
            fields,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>One row as a two-line CSV: header and RFC-quoted values.</summary>
    public string BuildRowCsv(DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        static string Quote(string value) =>
            value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        var header = string.Join(",", ResultColumns.Select(column => Quote(column.Name)));
        var values = string.Join(",", row.Cells.Select(cell =>
            cell.IsNull ? string.Empty : Quote(cell.Text)));
        return header + Environment.NewLine + values;
    }

    /// <summary>One row as an ANSI-quoted INSERT for the last previewed table.</summary>
    public string BuildRowSqlInsert(DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        static string QuoteIdentifier(string name) =>
            $"\"{name.Replace("\"", "\"\"")}\"";
        var table = QuoteIdentifier(_lastPreviewedTable ?? "table_name");
        var columns = string.Join(", ", ResultColumns.Select(column =>
            QuoteIdentifier(column.Name)));
        var values = string.Join(", ", ResultColumns.Zip(row.Cells, (column, cell) =>
            cell.IsNull
                ? "NULL"
                : CoerceTypedValue(column.DataTypeName, cell.Text) is bool or long or double or decimal
                    ? cell.Text
                    : $"'{cell.Text.Replace("'", "''")}'"));
        return $"INSERT INTO {table} ({columns}) VALUES ({values});";
    }

    /// <summary>
    /// Display text back to a typed value where the column type says the token
    /// is numeric or boolean; everything else stays a string.
    /// </summary>
    private static object CoerceTypedValue(string dataTypeName, string text)
    {
        var type = dataTypeName.ToUpperInvariant();
        if (type.Contains("BOOL") && bool.TryParse(text, out var flag))
        {
            return flag;
        }

        if (type.Contains("INT") && long.TryParse(text, out var integer))
        {
            return integer;
        }

        if ((type.Contains("REAL") || type.Contains("FLOAT") || type.Contains("DOUB")
                || type.Contains("NUM") || type.Contains("DEC"))
            && double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number;
        }

        return text;
    }

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
            foreach (var row in page.Rows)
            {
                longest = Math.Max(longest, row[index]?.Length ?? 4);
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
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (ConnectCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
        (RunQueryCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
    }
}
