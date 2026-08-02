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
            connectionString,
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
                        reader.GetString(0),
                        string.Equals(
                            reader.GetString(1),
                            "view",
                            StringComparison.OrdinalIgnoreCase)
                            ? DatabaseTableKind.View
                            : DatabaseTableKind.Table));
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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var driver = Resolve(driverId);
        return await ExecuteThroughTunnelAsync(
            driver,
            connectionString,
            tunnel,
            async (effectiveConnectionString, token) =>
            {
                var stopwatch = Stopwatch.StartNew();
                await using var connection = driver.CreateConnection(effectiveConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync(token)
                    .ConfigureAwait(false);
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(ordinal => new DatabaseColumnDescriptor(
                        reader.GetName(ordinal),
                        reader.GetDataTypeName(ordinal)))
                    .ToArray();
                var rows = new List<IReadOnlyList<string?>>();
                var truncated = false;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var cells = new string?[columns.Length];
                    for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                    {
                        cells[ordinal] = reader.IsDBNull(ordinal)
                            ? null
                            : RenderValue(reader.GetValue(ordinal));
                    }

                    rows.Add(cells);
                }

                stopwatch.Stop();
                return new DatabaseQueryPage(
                    columns,
                    rows,
                    truncated,
                    Math.Max(0, reader.RecordsAffected),
                    stopwatch.Elapsed);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Resolve(driverId).BuildPreviewQuery(tableName, limit);
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

    private static string RenderValue(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        byte[] bytes => $"0x{Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)))}"
            + (bytes.Length > 32 ? $"… ({bytes.Length} bytes)" : string.Empty),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
