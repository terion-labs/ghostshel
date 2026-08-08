using System.Data.Common;
using GhostShell.Application;

namespace GhostShell.Databases;

/// <summary>
/// One ADO.NET-backed database engine. Everything engine-specific — the
/// provider factory, schema catalog queries, identifier quoting, and paging
/// syntax — lives behind this boundary so the client stays generic.
/// </summary>
public interface IDatabaseDriver
{
    DatabaseDriverDescriptor Descriptor { get; }

    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Maps friendly input onto the provider's syntax before anything parses
    /// it — file engines accept a bare path here. The default keeps the input.
    /// </summary>
    string NormalizeConnectionString(string connectionString) => connectionString;

    /// <summary>
    /// A statement returning (catalog, schema, name, kind) rows for tables and
    /// views, ordered by qualified name. Missing qualification components are
    /// returned as SQL NULL; kind is the literal 'table' or 'view'.
    /// </summary>
    string ListTablesSql { get; }

    /// <summary>
    /// A statement returning one database name per row, for the database
    /// selector — or null when the engine has nothing to enumerate: file
    /// engines open exactly one file, Firebird names a server-side path, and
    /// Oracle's services resolve outside SQL. The descriptor's
    /// CanListDatabases must agree with this member.
    /// </summary>
    string? ListDatabasesSql => null;

    /// <summary>
    /// Session facts read from an already-open connection. The default answers
    /// with the provider's server version and no TLS fact; engines that can
    /// name their negotiated protocol override. A fact the server will not
    /// give up is null, never an exception — the connection itself is fine.
    /// </summary>
    ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DatabaseSessionInfo(
            DatabaseSessionProbes.TryGetServerVersion(connection)));

    string QuoteIdentifier(string identifier);

    string BuildPreviewQuery(string tableName, int limit);

    /// <summary>
    /// The network endpoint the connection string points at, or null for
    /// file-based engines that have nothing to tunnel.
    /// </summary>
    DatabaseEndpoint? GetEndpoint(string connectionString);

    /// <summary>Repoints the connection string at a forwarded local endpoint.</summary>
    string RewriteEndpoint(string connectionString, string host, int port);

    /// <summary>Decomposes a connection string for the details dialog.</summary>
    DatabaseConnectionDetails ParseDetails(string connectionString);

    /// <summary>Recomposes dialog fields into this engine's connection string.</summary>
    string BuildConnectionString(DatabaseConnectionDetails details);
}
