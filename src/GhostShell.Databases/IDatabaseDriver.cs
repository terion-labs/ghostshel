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
    /// A statement returning (name, kind) rows for tables and views, ordered by
    /// name, where kind is the literal 'table' or 'view'.
    /// </summary>
    string ListTablesSql { get; }

    string QuoteIdentifier(string identifier);

    string BuildPreviewQuery(string tableName, int limit);

    /// <summary>
    /// The network endpoint the connection string points at, or null for
    /// file-based engines that have nothing to tunnel.
    /// </summary>
    DatabaseEndpoint? GetEndpoint(string connectionString);

    /// <summary>Repoints the connection string at a forwarded local endpoint.</summary>
    string RewriteEndpoint(string connectionString, string host, int port);
}
