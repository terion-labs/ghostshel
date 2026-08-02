namespace GhostShell.Application;

/// <summary>One selectable database driver, described for pickers.</summary>
public sealed record DatabaseDriverDescriptor(
    string Id,
    string DisplayName,
    string ConnectionStringHint);

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

    /// <summary>Lists tables and views; also serves as the connectivity probe.</summary>
    Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes one statement. Result sets are capped at <paramref name="maxRows"/>
    /// rows; non-query statements return an empty column set with the affected count.
    /// </summary>
    Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        string sql,
        int maxRows,
        CancellationToken cancellationToken);

    /// <summary>A driver-quoted preview statement for one table.</summary>
    string BuildTablePreviewQuery(string driverId, string tableName, int limit);
}
