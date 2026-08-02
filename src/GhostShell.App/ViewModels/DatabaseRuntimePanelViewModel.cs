using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class DatabaseDriverOptionViewModel(DatabaseDriverDescriptor descriptor)
{
    public string Id { get; } = descriptor.Id;

    public string DisplayName { get; } = descriptor.DisplayName;

    public string ConnectionStringHint { get; } = descriptor.ConnectionStringHint;
}

public sealed class DatabaseTableItemViewModel(DatabaseTableDescriptor table)
{
    public string Name { get; } = table.Name;

    public string KindLabel { get; } = table.Kind == DatabaseTableKind.View ? "View" : "Table";
}

public sealed class DatabaseResultCellViewModel(string? text)
{
    public bool IsNull { get; } = text is null;

    public string Text { get; } = text ?? "NULL";
}

public sealed class DatabaseResultRowViewModel(int number, IReadOnlyList<string?> cells)
    : ObservableObject
{
    private bool _isSelected;

    /// <summary>The 1-based position inside the current result page.</summary>
    public int Number { get; } = number;

    public bool IsEven => Number % 2 == 0;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public IReadOnlyList<DatabaseResultCellViewModel> Cells { get; } =
        cells.Select(cell => new DatabaseResultCellViewModel(cell)).ToArray();
}

/// <summary>One field of the selected row, presented in the inspector column.</summary>
public sealed record DatabaseRowFieldViewModel(
    string Name,
    string DataTypeName,
    string Text,
    bool IsNull);

public sealed class DatabaseResultColumnViewModel(DatabaseColumnDescriptor column)
{
    public string Name { get; } = column.Name;

    public string DataTypeName { get; } = column.DataTypeName;
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
    private readonly CancellationTokenSource _lifetime = new();
    private DatabaseDriverOptionViewModel _selectedDriver;
    private string _connectionString = string.Empty;
    private string _queryText = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private string? _errorMessage;
    private string _resultSummary = string.Empty;
    private IReadOnlyList<DatabaseResultColumnViewModel> _resultColumns = [];
    private IReadOnlyList<DatabaseResultRowViewModel> _resultRows = [];
    private DatabaseResultRowViewModel? _selectedRow;
    private IReadOnlyList<DatabaseRowFieldViewModel> _selectedRowFields = [];

    public DatabaseRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IDatabasePanelClient client,
        string? driverId = null,
        string? connectionString = null)
        : base(id, PanelKind.DatabaseViewer, title, "Database")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        DriverOptions = client.Drivers
            .Select(descriptor => new DatabaseDriverOptionViewModel(descriptor))
            .ToArray();
        if (DriverOptions.Count == 0)
        {
            throw new ArgumentException(
                "The database client exposes no drivers.",
                nameof(client));
        }

        _selectedDriver = DriverOptions.FirstOrDefault(option =>
                string.Equals(option.Id, driverId, StringComparison.Ordinal))
            ?? DriverOptions[0];
        _connectionString = connectionString ?? string.Empty;
        ConnectCommand = new AsyncActionCommand(ConnectAsync, () => !IsBusy);
        RunQueryCommand = new AsyncActionCommand(RunQueryAsync, () => !IsBusy && IsConnected);
        // A restored panel reconnects on its own: the saved target is the whole
        // point of persisting it.
        Initialization = driverId is not null && !string.IsNullOrWhiteSpace(connectionString)
            ? ConnectAsync()
            : Task.CompletedTask;
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
            }
        }
    }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value ?? string.Empty);
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
    }

    /// <summary>
    /// The durable "driverId:connection string" address, or null while the
    /// panel has no usable target. Recovery and workspace autosave persist it.
    /// </summary>
    public string? RecoveryTarget => string.IsNullOrWhiteSpace(ConnectionString)
        ? null
        : new DatabasePanelTarget(SelectedDriver.Id, ConnectionString).Serialize();

    public async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            ErrorMessage = "Enter a connection string first.";
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            var tables = await _client.ListTablesAsync(
                SelectedDriver.Id,
                ConnectionString,
                cancellationToken);
            Tables.Clear();
            foreach (var table in tables)
            {
                Tables.Add(new DatabaseTableItemViewModel(table));
            }

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
        QueryText = preview;
        await ExecuteQueryAsync(preview);
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.Dispose();
    }

    private async Task ExecuteQueryAsync(string sql) =>
        await RunGuardedAsync(async cancellationToken =>
        {
            var page = await _client.QueryAsync(
                SelectedDriver.Id,
                ConnectionString,
                sql,
                MaxRows,
                cancellationToken);
            SelectRow(null);
            ResultColumns = page.Columns
                .Select(column => new DatabaseResultColumnViewModel(column))
                .ToArray();
            ResultRows = page.Rows
                .Select((row, index) => new DatabaseResultRowViewModel(index + 1, row))
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
