using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Databases;

/// <summary>
/// The generic ADO.NET query surface behind <see cref="IDatabasePanelClient"/>.
/// Connections are opened per call — ADO.NET pooling makes that cheap for the
/// server drivers, and it keeps a panel from pinning a file lock or a socket
/// while idle. SSH tunnels are the exception: a handshake per statement would
/// dominate every query, so opened forwards are cached per connection and
/// target, and evicted on the first failure so a dropped tunnel reopens.
/// </summary>
public sealed class DatabasePanelClient : IDatabasePanelClient, IAsyncDisposable
{
    private const int MaximumTablePageSize = 5000;

    private readonly IReadOnlyDictionary<string, IDatabaseDriver> _drivers;
    private readonly IDatabaseTunnelFactory? _tunnelFactory;
    private readonly ConcurrentDictionary<
        (string ConnectionId, string Host, int Port),
        Task<IDatabaseTunnelLease>> _tunnels = new();

    public DatabasePanelClient(IDatabaseTunnelFactory? tunnelFactory = null)
        : this(BuiltInDatabaseDrivers.All, tunnelFactory)
    {
    }

    public DatabasePanelClient(
        IReadOnlyList<IDatabaseDriver> drivers,
        IDatabaseTunnelFactory? tunnelFactory = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = drivers.ToDictionary(
            driver => driver.Descriptor.Id,
            StringComparer.Ordinal);
        _tunnelFactory = tunnelFactory;
        Drivers = drivers.Select(driver => driver.Descriptor).ToArray();
    }

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    public async Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = driver.ListTablesSql;
                var tables = new List<DatabaseTableDescriptor>();
                await using var reader = await command.ExecuteReaderAsync(token)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    tables.Add(new DatabaseTableDescriptor(
                        reader.GetString(2),
                        string.Equals(
                            reader.GetString(3).Trim(),
                            "view",
                            StringComparison.OrdinalIgnoreCase)
                            ? DatabaseTableKind.View
                            : DatabaseTableKind.Table,
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1)));
                }

                return (IReadOnlyList<DatabaseTableDescriptor>)tables;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        await QueryCoreAsync(
            driverId,
            connectionString,
            tunnel,
            sql,
            maxRows,
            requestKeyInfo: false,
            cancellationToken).ConfigureAwait(false);

    public async Task<DatabaseQueryPage> QueryWithProvenanceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        await QueryCoreAsync(
            driverId,
            connectionString,
            tunnel,
            sql,
            maxRows,
            // FirebirdSql.Data can leave an otherwise valid result cursor
            // unusable when CommandBehavior.KeyInfo is requested for a
            // projection such as SELECT 1 FROM RDB$DATABASE. Do not retry the
            // statement after that failure: SELECT may invoke user functions,
            // so running it twice is not a safe metadata fallback. Firebird's
            // ordinary column schema remains the conservative provenance
            // source and simply fails closed when the provider omits lineage.
            requestKeyInfo: IsResultQuery(sql)
                && !string.Equals(driverId, "firebird", StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);

    private async Task<DatabaseQueryPage> QueryCoreAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        bool requestKeyInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var driver = Resolve(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                var result = await ExecuteQueryAsync(
                        connection,
                        new DatabaseSqlCommand(sql, []),
                        maxRows,
                        schema: null,
                        token,
                        requestKeyInfo)
                    .ConfigureAwait(false);
                if (requestKeyInfo
                    && string.Equals(driverId, "duckdb", StringComparison.Ordinal))
                {
                    result = await DuckDbQueryProvenance.EnrichAsync(
                            connection,
                            sql,
                            result,
                            token)
                        .ConfigureAwait(false);
                }

                return requestKeyInfo
                    ? NormalizeProviderProvenance(driverId, result)
                    : result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        var driver = Resolve(driverId);
        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, databaseObject, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseTablePage> ReadQueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumTablePageSize);
        var driver = Resolve(driverId);
        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                var requestedLimit = query.Limit;
                var readQuery = query with { Limit = requestedLimit + 1 };
                var command = dialect.BuildQuerySelect(sourceSql, sourceColumns, readQuery);
                var result = await ExecuteQueryAsync(
                        connection,
                        command,
                        readQuery.Limit,
                        schema: null,
                        token)
                    .ConfigureAwait(false);
                result = PreserveQueryColumnContext(result, sourceColumns);
                var hasLookAheadRow = result.ValueRows.Count > requestedLimit;
                var pageResult = hasLookAheadRow
                    ? result with
                    {
                        Rows = result.Rows.Take(requestedLimit).ToArray(),
                        TypedRows = result.ValueRows.Take(requestedLimit).ToArray(),
                        Truncated = true,
                    }
                    : result;
                var totalRows = await ExecuteCountAsync(
                        connection,
                        dialect.BuildQueryCount(sourceSql, sourceColumns, query.Filters),
                        token)
                    .ConfigureAwait(false);
                return new DatabaseTablePage(
                    pageResult,
                    query.Offset,
                    requestedLimit,
                    hasLookAheadRow,
                    totalRows);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountQueryRowsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(filters);
        var driver = Resolve(driverId);
        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await ExecuteCountAsync(
                        connection,
                        dialect.BuildQueryCount(sourceSql, sourceColumns, filters),
                        token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseTablePage> ReadTableAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumTablePageSize);
        var driver = Resolve(driverId);
        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                var details = await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, table, token, includeIndexes: false)
                    .ConfigureAwait(false);
                var canPage = details.PrimaryKey.Count > 0;
                if (!canPage && query.Offset > 0)
                {
                    throw new InvalidOperationException(
                        "This object has no primary key, so only its first page can be read safely.");
                }

                var requestedLimit = query.Limit;
                var readQuery = query with
                {
                    Limit = query.Limit + 1,
                };
                var command = dialect.BuildSelect(table.Id, details.Columns, readQuery);
                var result = await ExecuteQueryAsync(
                        connection,
                        command,
                        readQuery.Limit,
                        details.Columns,
                        token)
                    .ConfigureAwait(false);
                var hasLookAheadRow = result.ValueRows.Count > requestedLimit;
                var pageResult = hasLookAheadRow
                    ? result with
                    {
                        Rows = result.Rows.Take(requestedLimit).ToArray(),
                        TypedRows = result.ValueRows.Take(requestedLimit).ToArray(),
                        Truncated = true,
                    }
                    : result;
                var totalRows = await ExecuteCountAsync(
                        connection,
                        dialect.BuildCount(table.Id, details.Columns, query.Filters),
                        token)
                    .ConfigureAwait(false);
                return new DatabaseTablePage(
                    pageResult,
                    query.Offset,
                    requestedLimit,
                    hasLookAheadRow && canPage,
                    totalRows);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseMutationResult> ApplyTableChangesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.IsEmpty)
        {
            return new DatabaseMutationResult(0, 0, 0);
        }

        var driver = Resolve(driverId);
        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                var details = await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, table, token, includeIndexes: false)
                    .ConfigureAwait(false);
                if (!details.CanEdit)
                {
                    throw new InvalidOperationException(details.ReadOnlyReason ?? "This table is read-only.");
                }

                return await ApplyChangesAsync(connection, dialect, details, changes, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Resolve(driverId).BuildPreviewQuery(tableName, limit);
    }

    public string BuildInsertStatement(
        string driverId,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(row);
        if (details.Object.Kind != DatabaseTableKind.Table)
        {
            throw new InvalidOperationException("INSERT scripts require a physical table.");
        }

        return DatabaseSqlDialect.For(driverId)
            .BuildInsertStatement(details.Object.Id, details, row);
    }

    public string BuildTablePreviewQuery(string driverId, DatabaseObjectId table, int limit)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return DatabaseSqlDialect.For(driverId)
            .BuildSelect(table, [], DatabaseTableQuery.FirstPage(limit))
            .Sql;
    }

    public DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString) =>
        ResolveDetails(Resolve(driverId), connectionString ?? string.Empty);

    /// <summary>
    /// A URL pasted into the connection box fills the host, port, database and
    /// credential fields too, rather than sitting there as one opaque line —
    /// so the engines that speak one are asked to translate before the fields
    /// are read out of it.
    /// </summary>
    private static DatabaseConnectionDetails ResolveDetails(
        IDatabaseDriver driver,
        string connectionString)
    {
        try
        {
            return driver.ParseDetails(driver.NormalizeConnectionString(connectionString));
        }
        catch (ArgumentException)
        {
            // A URL naming something this build cannot honour is still worth
            // showing as it was typed; the refusal is raised when it is used.
            return driver.ParseDetails(connectionString);
        }
    }

    public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return Resolve(driverId).BuildConnectionString(details);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pending in _tunnels.Values)
        {
            try
            {
                var lease = await pending.ConfigureAwait(false);
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Teardown must reach every forward; a dead one is already down.
            }
        }

        _tunnels.Clear();
    }

    private async Task<TResult> ExecuteThroughTunnelAsync<TResult>(
        IDatabaseDriver driver,
        string connectionString,
        ConnectionProfile? tunnel,
        Func<string, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (tunnel is null)
        {
            return await operation(connectionString, cancellationToken).ConfigureAwait(false);
        }

        if (_tunnelFactory is null)
        {
            throw new InvalidOperationException(
                "SSH tunneling is unavailable in this build.");
        }

        var endpoint = driver.GetEndpoint(connectionString)
            ?? throw new InvalidOperationException(
                $"{driver.Descriptor.DisplayName} connections cannot be tunneled: "
                + "the connection string has no network endpoint.");
        var key = (tunnel.Id.Value, endpoint.Host, endpoint.Port);
        try
        {
            var lease = await _tunnels.GetOrAdd(
                    key,
                    _ => OpenTunnelAsync(tunnel, endpoint, cancellationToken))
                .ConfigureAwait(false);
            return await operation(
                    driver.RewriteEndpoint(connectionString, "127.0.0.1", lease.LocalPort),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // A failure may mean the forward died; drop it so the next attempt
            // opens a fresh one instead of failing forever.
            if (_tunnels.TryRemove(key, out var stale))
            {
                _ = DisposeQuietlyAsync(stale);
            }

            throw;
        }
    }

    private async Task<IDatabaseTunnelLease> OpenTunnelAsync(
        ConnectionProfile connection,
        DatabaseEndpoint endpoint,
        CancellationToken cancellationToken) =>
        await _tunnelFactory!.OpenAsync(
                connection,
                endpoint.Host,
                endpoint.Port,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task DisposeQuietlyAsync(Task<IDatabaseTunnelLease> pending)
    {
        try
        {
            var lease = await pending.ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The forward is gone either way.
        }
    }

    private IDatabaseDriver Resolve(string driverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        return _drivers.TryGetValue(driverId, out var driver)
            ? driver
            : throw new ArgumentException(
                $"The database driver '{driverId}' is not available in this build.",
                nameof(driverId));
    }

    private static async Task<DatabaseQueryPage> ExecuteQueryAsync(
        DbConnection connection,
        DatabaseSqlCommand statement,
        int maxRows,
        IReadOnlyList<DatabaseColumnSchema>? schema,
        CancellationToken cancellationToken,
        bool requestKeyInfo = false)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var command = CreateCommand(connection, statement);
        var behavior = requestKeyInfo ? CommandBehavior.KeyInfo : CommandBehavior.Default;
        await using var reader = await command.ExecuteReaderAsync(behavior, cancellationToken)
            .ConfigureAwait(false);
        var described = DatabaseValueMaterializer.DescribeColumns(reader);
        var visibleOrdinals = described
            .Select((column, ordinal) => (column, ordinal))
            .Where(item => !item.column.IsHidden)
            .ToArray();
        var columns = schema is null
            ? visibleOrdinals.Select(item => item.column).ToArray()
            : visibleOrdinals
                .Select(item => MergeSchema(item.column, schema))
                .ToArray();
        var values = new List<IReadOnlyList<DatabaseValue>>();
        var displayRows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (values.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var typedRow = new DatabaseValue[columns.Length];
            var displayRow = new string?[columns.Length];
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            {
                var readerOrdinal = visibleOrdinals[ordinal].ordinal;
                var value = DatabaseValueMaterializer.Materialize(
                    reader,
                    readerOrdinal,
                    columns[ordinal]);
                typedRow[ordinal] = value;
                displayRow[ordinal] = value.IsNull ? null : value.DisplayText;
            }

            values.Add(typedRow);
            displayRows.Add(displayRow);
        }

        stopwatch.Stop();
        var safeColumns = DatabaseValueMaterializer.ReconcileColumnSafety(columns, values);
        return new DatabaseQueryPage(
            safeColumns,
            displayRows,
            truncated,
            Math.Max(0, reader.RecordsAffected),
            stopwatch.Elapsed,
            values);
    }

    private static bool IsResultQuery(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        return statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static DatabaseColumnDescriptor MergeSchema(
        DatabaseColumnDescriptor column,
        IReadOnlyList<DatabaseColumnSchema> schema)
    {
        var metadata = schema.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
        return metadata is null
            ? column
            : column with
            {
                DataTypeName = metadata.DataTypeName,
                ValueKind = metadata.ValueKind == DatabaseValueKind.Other
                    ? column.ValueKind
                    : metadata.ValueKind,
                IsNullable = metadata.IsNullable,
                IsKey = metadata.IsPrimaryKey,
                IsIdentity = metadata.IsIdentity,
                IsReadOnly = !metadata.CanEdit,
                BaseColumnName = metadata.Name,
                DefaultExpression = metadata.DefaultExpression,
            };
    }

    private static DatabaseQueryPage PreserveQueryColumnContext(
        DatabaseQueryPage result,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns)
    {
        if (result.Columns.Count != sourceColumns.Count)
        {
            return result;
        }

        var columns = result.Columns
            .Select((column, ordinal) =>
            {
                var source = sourceColumns[ordinal];
                return string.Equals(column.Name, source.Name, StringComparison.Ordinal)
                    ? column with
                    {
                        IsNullable = source.IsNullable,
                        IsKey = source.IsKey,
                        IsIdentity = source.IsIdentity,
                        // Several providers describe every column projected by
                        // our derived-table wrapper as read-only even when the
                        // proven source column is writable. The wrapper is a
                        // browsing implementation detail; mutation safety comes
                        // from the exact source provenance and table metadata.
                        // Provider-owned values still fail closed later through
                        // ReconcileColumnSafety/HasDisplayOnlyValue.
                        IsReadOnly = source.IsReadOnly,
                        BaseColumnName = source.BaseColumnName,
                        DefaultExpression = source.DefaultExpression,
                        BaseObject = source.BaseObject,
                    }
                    : column;
            })
            .ToArray();
        return result with { Columns = columns };
    }

    private static DatabaseQueryPage NormalizeProviderProvenance(
        string driverId,
        DatabaseQueryPage result)
    {
        if (!string.Equals(driverId, "sqlite", StringComparison.Ordinal))
        {
            return result;
        }

        var columns = result.Columns
            .Select(column => column.BaseObject is { } source
                    && string.Equals(source.Catalog, "main", StringComparison.OrdinalIgnoreCase)
                ? column with { BaseObject = source with { Catalog = null } }
                : column)
            .ToArray();
        return result with { Columns = columns };
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DatabaseSqlCommand statement,
        DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = statement.Sql;
        command.Transaction = transaction;
        foreach (var value in statement.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task<long> ExecuteCountAsync(
        DbConnection connection,
        DatabaseSqlCommand statement,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, statement);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("The database did not return a row count.");
        }

        long count;
        try
        {
            count = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException
            or InvalidCastException
            or OverflowException)
        {
            throw new InvalidOperationException(
                "The database returned an invalid row count.",
                exception);
        }

        return count >= 0
            ? count
            : throw new InvalidOperationException("The database returned a negative row count.");
    }

    private static async Task<DatabaseMutationResult> ApplyChangesAsync(
        DbConnection connection,
        DatabaseSqlDialect dialect,
        DatabaseObjectDetails details,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = 0;
        var updated = 0;
        var deleted = 0;
        try
        {
            foreach (var row in changes.Inserts)
            {
                inserted += await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildInsert(details.Object.Id, details, row),
                        expectSingleRow: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var row in changes.Updates)
            {
                var affected = await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildUpdate(details.Object.Id, details, row),
                        expectSingleRow: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new DatabaseMutationResult(0, 0, 0, true, "The row changed since it was loaded.");
                }

                updated += affected;
            }

            foreach (var row in changes.Deletes)
            {
                var affected = await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildDelete(details.Object.Id, details, row),
                        expectSingleRow: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new DatabaseMutationResult(0, 0, 0, true, "The row changed since it was loaded.");
                }

                deleted += affected;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseMutationResult(inserted, updated, deleted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ExecuteMutationAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseSqlCommand statement,
        bool expectSingleRow,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, statement, transaction);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected < 0 || affected > 1 || (expectSingleRow && affected != 1))
        {
            throw new InvalidOperationException(
                $"A row mutation affected {affected} rows; exactly one was expected.");
        }

        return affected;
    }
}
