using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using GhostShell.Application;

namespace GhostShell.Databases;

/// <summary>
/// The generic ADO.NET query surface behind <see cref="IDatabasePanelClient"/>.
/// Connections are opened per call — ADO.NET pooling makes that cheap for the
/// server drivers, and it keeps a panel from pinning a file lock or a socket
/// while idle.
/// </summary>
public sealed class DatabasePanelClient : IDatabasePanelClient
{
    private readonly IReadOnlyDictionary<string, IDatabaseDriver> _drivers;

    public DatabasePanelClient()
        : this(BuiltInDatabaseDrivers.All)
    {
    }

    public DatabasePanelClient(IReadOnlyList<IDatabaseDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = drivers.ToDictionary(
            driver => driver.Descriptor.Id,
            StringComparer.Ordinal);
        Drivers = drivers.Select(driver => driver.Descriptor).ToArray();
    }

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    public async Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = driver.ListTablesSql;
        var tables = new List<DatabaseTableDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new DatabaseTableDescriptor(
                reader.GetString(0),
                string.Equals(reader.GetString(1), "view", StringComparison.OrdinalIgnoreCase)
                    ? DatabaseTableKind.View
                    : DatabaseTableKind.Table));
        }

        return tables;
    }

    public async Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        string sql,
        int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var driver = Resolve(driverId);
        var stopwatch = Stopwatch.StartNew();
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(ordinal => new DatabaseColumnDescriptor(
                reader.GetName(ordinal),
                reader.GetDataTypeName(ordinal)))
            .ToArray();
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Resolve(driverId).BuildPreviewQuery(tableName, limit);
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
