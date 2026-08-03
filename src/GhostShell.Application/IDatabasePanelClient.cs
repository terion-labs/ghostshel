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
    DatabaseTableKind Kind);

public enum DatabaseTableKind
{
    Table,
    View,
}

public sealed record DatabaseColumnDescriptor(
    string Name,
    string DataTypeName);

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
    TimeSpan Elapsed);

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

    /// <summary>A driver-quoted preview statement for one table.</summary>
    string BuildTablePreviewQuery(string driverId, string tableName, int limit);

    /// <summary>Decomposes a connection string into structural fields.</summary>
    DatabaseConnectionDetails ParseConnectionDetails(string driverId, string connectionString);

    /// <summary>Recomposes structural fields into the driver's connection string.</summary>
    string BuildConnectionString(string driverId, DatabaseConnectionDetails details);
}
