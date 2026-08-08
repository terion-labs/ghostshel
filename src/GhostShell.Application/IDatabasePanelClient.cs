using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>One selectable database driver, described for pickers.</summary>
public sealed record DatabaseDriverDescriptor(
    string Id,
    string DisplayName,
    string ConnectionStringHint,
    bool IsFileBased = false);

/// <summary>
/// The structural view of a connection string, for the details dialog. File
/// engines use <see cref="FilePath"/> only; server engines use the endpoint
/// and credential fields, with unrecognized parameters preserved verbatim in
/// <see cref="Options"/>.
/// </summary>
public sealed record DatabaseConnectionDetails(
    string? Host = null,
    int? Port = null,
    string? Database = null,
    string? Username = null,
    string? Password = null,
    string? FilePath = null,
    string? Options = null);

/// <summary>The network endpoint a connection string points at.</summary>
public sealed record DatabaseEndpoint(string Host, int Port);

/// <summary>A held local port-forward. Disposing tears the forward down.</summary>
public interface IDatabaseTunnelLease : IAsyncDisposable
{
    int LocalPort { get; }
}

/// <summary>
/// Opens SSH local port-forwards for database connections. The implementation
/// owns the SSH client, credential resolution, and host-key enforcement; the
/// database engine only sees a loopback endpoint.
/// </summary>
public interface IDatabaseTunnelFactory
{
    ValueTask<IDatabaseTunnelLease> OpenAsync(
        ConnectionProfile connection,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken);
}

/// <summary>A table or view visible to the connected principal.</summary>
public sealed record DatabaseTableDescriptor(
    string Name,
    DatabaseTableKind Kind,
    string? Catalog = null,
    string? Schema = null)
{
    public DatabaseObjectId Id => new(Catalog, Schema, Name);

    public string DisplayName => Id.DisplayName;
}

public enum DatabaseTableKind
{
    Table,
    View,
}

public sealed record DatabaseColumnDescriptor(
    string Name,
    string DataTypeName,
    DatabaseValueKind ValueKind = DatabaseValueKind.Other,
    string? ClrTypeName = null,
    bool? IsNullable = null,
    bool IsKey = false,
    bool IsIdentity = false,
    bool IsReadOnly = false,
    string? BaseColumnName = null,
    string? DefaultExpression = null,
    DatabaseObjectId? BaseObject = null,
    bool IsHidden = false);

/// <summary>
/// One bounded query result. Rows are pre-rendered to display text because the
/// viewer presents values rather than computing over them; null cells stay null
/// so the presentation can distinguish NULL from an empty string.
/// </summary>
public sealed record DatabaseQueryPage(
    IReadOnlyList<DatabaseColumnDescriptor> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int RowsAffected,
    TimeSpan Elapsed,
    IReadOnlyList<IReadOnlyList<DatabaseValue>>? TypedRows = null)
{
    /// <summary>
    /// Typed values when supplied by the provider client, otherwise a compatible
    /// text projection for lightweight clients and older saved test fixtures.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<DatabaseValue>> ValueRows => TypedRows
        ?? Rows
            .Select(row => (IReadOnlyList<DatabaseValue>)row
                .Select((value, index) => DatabaseValue.FromDisplay(
                    value,
                    index < Columns.Count ? Columns[index].ValueKind : DatabaseValueKind.Text))
                .ToArray())
            .ToArray();
}

/// <summary>
/// The application-facing boundary of the multi-driver database engine. The
/// presentation layer depends on this contract only; the concrete ADO.NET
/// drivers live in GhostShell.Databases and are composed in the desktop host.
/// Implementations open a connection per call and rely on ADO.NET pooling, so a
/// panel holds no connection state between operations.
/// </summary>
public interface IDatabasePanelClient
{
    IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    /// <summary>
    /// Lists tables and views; also serves as the connectivity probe. A non-null
    /// <paramref name="tunnel"/> routes the connection through an SSH local
    /// port-forward over that profile.
    /// </summary>
    Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes one statement. Result sets are capped at <paramref name="maxRows"/>
    /// rows; non-query statements return an empty column set with the affected count.
    /// </summary>
    Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a result query while asking the provider for base-table lineage.
    /// Providers may supply no lineage; callers must still fail closed before
    /// enabling row mutations.
    /// </summary>
    Task<DatabaseQueryPage> QueryWithProvenanceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        QueryAsync(
            driverId,
            connectionString,
            tunnel,
            sql,
            maxRows,
            cancellationToken);

    /// <summary>
    /// Re-reads a successful result-producing statement through a bounded,
    /// provider-owned outer query. This keeps filtering and ordering on the
    /// database instead of sorting only the currently materialized page.
    /// </summary>
    Task<DatabaseTablePage> ReadQueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken) =>
        Task.FromException<DatabaseTablePage>(new NotSupportedException(
            "This database client does not expose structured query-result browsing."));

    /// <summary>
    /// Counts the rows produced by a successful, repeatable result query after
    /// applying the same typed filters used by the page reader.
    /// </summary>
    Task<long> CountQueryRowsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken) =>
        Task.FromException<long>(new NotSupportedException(
            "This database client does not expose query-result row counts."));

    Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken) =>
        Task.FromException<DatabaseObjectDetails>(new NotSupportedException(
            "This database client does not expose structure metadata."));

    Task<DatabaseTablePage> ReadTableAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken) =>
        Task.FromException<DatabaseTablePage>(new NotSupportedException(
            "This database client does not expose typed table browsing."));

    Task<DatabaseMutationResult> ApplyTableChangesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken) =>
        Task.FromException<DatabaseMutationResult>(new NotSupportedException(
            "This database client does not expose row editing."));

    /// <summary>
    /// Builds one executable INSERT statement using the active driver's
    /// identifier, literal, and type rules. The row must already be mapped to
    /// the supplied physical table metadata.
    /// </summary>
    string BuildInsertStatement(
        string driverId,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row) =>
        throw new NotSupportedException(
            "This database client does not expose INSERT script generation.");

    /// <summary>A driver-quoted preview statement for one table.</summary>
    string BuildTablePreviewQuery(string driverId, string tableName, int limit);

    /// <summary>A driver-quoted preview statement preserving qualification.</summary>
    string BuildTablePreviewQuery(string driverId, DatabaseObjectId table, int limit) =>
        BuildTablePreviewQuery(driverId, table.Name, limit);

    /// <summary>Decomposes a connection string into structural fields.</summary>
    DatabaseConnectionDetails ParseConnectionDetails(string driverId, string connectionString);

    /// <summary>Recomposes structural fields into the driver's connection string.</summary>
    string BuildConnectionString(string driverId, DatabaseConnectionDetails details);
}
