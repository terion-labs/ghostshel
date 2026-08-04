using System.Data.Common;
using ClickHouse.Client.ADO;
using DuckDB.NET.Data;
using FirebirdSql.Data.FirebirdClient;
using GhostShell.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace GhostShell.Databases;

/// <summary>
/// The drivers this build ships. Ids are durable: saved panels store them.
/// Engines that speak another engine's wire protocol (MariaDB, CockroachDB,
/// Redshift) get their own id and hint but reuse the compatible provider, so a
/// saved panel names what the user actually connected to.
/// </summary>
public static class BuiltInDatabaseDrivers
{
    public static IReadOnlyList<IDatabaseDriver> All { get; } =
    [
        new SqliteDatabaseDriver(),
        new PostgresFamilyDriver(
            "postgres",
            "PostgreSQL",
            "Host=localhost;Port=5432;Database=app;Username=postgres;Password=…"),
        new MySqlFamilyDriver(
            "mysql",
            "MySQL",
            "Server=localhost;Port=3306;Database=app;User ID=root;Password=…"),
        new MySqlFamilyDriver(
            "mariadb",
            "MariaDB",
            "Server=localhost;Port=3306;Database=app;User ID=root;Password=…"),
        new SqlServerDatabaseDriver(),
        new PostgresFamilyDriver(
            "cockroach",
            "CockroachDB",
            "Host=localhost;Port=26257;Database=app;Username=root;SSL Mode=Disable"),
        new PostgresFamilyDriver(
            "redshift",
            "Amazon Redshift",
            "Host=cluster.region.redshift.amazonaws.com;Port=5439;Database=app;Username=…;Password=…"),
        new DuckDbDatabaseDriver(),
        new OracleDatabaseDriver(),
        new FirebirdDatabaseDriver(),
        new ClickHouseDatabaseDriver(),
    ];
}

/// <summary>
/// File-based engines take a bare path in the viewer: "/data/app.db" (or
/// "~/app.db") becomes "Data Source=…" before the provider parses it. Anything
/// containing '=' is already a connection string and passes through.
/// </summary>
internal static class FileConnectionStrings
{
    public static string Normalize(string connectionString)
    {
        var value = connectionString.Trim();
        if (value.Length == 0 || value.Contains('='))
        {
            return connectionString;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[2..]);
        }

        return $"Data Source={value}";
    }
}

internal sealed class SqliteDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "sqlite",
        "SQLite",
        "/path/to/database.db",
        IsFileBased: true);

    public DbConnection CreateConnection(string connectionString) =>
        SqliteInMemoryDatabases.TryCreateConnection(connectionString)
            ?? new SqliteConnection(connectionString);

    public string NormalizeConnectionString(string connectionString) =>
        connectionString.Contains(
            SqliteInMemoryDatabases.TokenPrefix,
            StringComparison.Ordinal)
            // A memory token is already exact; path normalization would only
            // mangle it.
            ? connectionString
            : FileConnectionStrings.Normalize(connectionString);

    public string ListTablesSql => """
        SELECT name, type FROM sqlite_master
        WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
        ORDER BY name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString) => null;

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        throw new InvalidOperationException(
            "SQLite databases are files and cannot be tunneled.");

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var normalized = FileConnectionStrings.Normalize(connectionString);
        var builder = ConnectionDetailKeys.TryCreateBuilder(normalized);
        if (builder is null)
        {
            return new DatabaseConnectionDetails(FilePath: connectionString.Trim());
        }

        var path = ConnectionDetailKeys.Take(builder, ["Data Source", "DataSource"]);
        return new DatabaseConnectionDetails(
            FilePath: path,
            Options: builder.Count == 0 ? null : builder.ConnectionString);
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.Options))
        {
            return details.FilePath?.Trim() ?? string.Empty;
        }

        var builder = ConnectionDetailKeys.TryCreateBuilder(details.Options)
            ?? new System.Data.Common.DbConnectionStringBuilder();
        ConnectionDetailKeys.Set(builder, ["Data Source", "DataSource"], details.FilePath?.Trim());
        return builder.ConnectionString;
    }
}

/// <summary>PostgreSQL and the engines that speak its wire protocol.</summary>
internal sealed class PostgresFamilyDriver(
    string id,
    string displayName,
    string connectionStringHint) : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        id,
        displayName,
        connectionStringHint);

    public DbConnection CreateConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    public string ListTablesSql => """
        SELECT table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
        ORDER BY table_name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Host)
            ? null
            : new DatabaseEndpoint(builder.Host, builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = host,
            Port = port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Host", "Server"],
        ["Port"],
        ["Database", "DB"],
        ["Username", "User ID", "UserName", "User Id"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

/// <summary>MySQL and MariaDB, which share MySqlConnector.</summary>
internal sealed class MySqlFamilyDriver(
    string id,
    string displayName,
    string connectionStringHint) : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        id,
        displayName,
        connectionStringHint);

    public DbConnection CreateConnection(string connectionString) =>
        new MySqlConnection(connectionString);

    public string ListTablesSql => """
        SELECT table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
        ORDER BY table_name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Server)
            ? null
            : new DatabaseEndpoint(builder.Server, (int)builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new MySqlConnectionStringBuilder(connectionString)
        {
            Server = host,
            Port = (uint)port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Server", "Host", "Data Source"],
        ["Port"],
        ["Database"],
        ["User ID", "UserID", "User", "Username", "Uid"],
        ["Password", "Pwd"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

internal sealed class SqlServerDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "sqlserver",
        "SQL Server",
        "Server=localhost,1433;Database=app;User ID=sa;Password=…;TrustServerCertificate=True");

    public DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    public string ListTablesSql => """
        SELECT TABLE_NAME,
               CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM INFORMATION_SCHEMA.TABLES
        ORDER BY TABLE_NAME;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]")}]";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT TOP ({limit}) * FROM {QuoteIdentifier(tableName)};";

    // SQL Server addresses are "host" or "host,port" in DataSource.
    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var source = new SqlConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var parts = source.Split(',', 2);
        return new DatabaseEndpoint(
            parts[0].Trim(),
            parts.Length == 2 && int.TryParse(parts[1], out var port) ? port : 1433);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            DataSource = $"{host},{port}",
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Server", "Data Source", "Address", "Addr", "Network Address"],
        [],
        ["Database", "Initial Catalog"],
        ["User ID", "UID"],
        ["Password", "PWD"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var details = DetailKeys.Parse(connectionString);
        if (details.Host is { } packed)
        {
            var parts = packed.Split(',', 2);
            details = details with
            {
                Host = parts[0].Trim(),
                Port = parts.Length == 2 && int.TryParse(parts[1], out var port)
                    ? port
                    : details.Port,
            };
        }

        return details;
    }

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details with
        {
            Host = details.Port is { } port && !string.IsNullOrWhiteSpace(details.Host)
                ? $"{details.Host},{port}"
                : details.Host,
            Port = null,
        });
}

internal sealed class DuckDbDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "duckdb",
        "DuckDB",
        "/path/to/analytics.duckdb",
        IsFileBased: true);

    public DbConnection CreateConnection(string connectionString) =>
        new DuckDBConnection(connectionString);

    public string NormalizeConnectionString(string connectionString) =>
        FileConnectionStrings.Normalize(connectionString);

    public string ListTablesSql => """
        SELECT table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
        ORDER BY table_name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString) => null;

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        throw new InvalidOperationException(
            "DuckDB databases are files and cannot be tunneled.");

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var normalized = FileConnectionStrings.Normalize(connectionString);
        var builder = ConnectionDetailKeys.TryCreateBuilder(normalized);
        if (builder is null)
        {
            return new DatabaseConnectionDetails(FilePath: connectionString.Trim());
        }

        var path = ConnectionDetailKeys.Take(builder, ["Data Source", "DataSource"]);
        return new DatabaseConnectionDetails(
            FilePath: path,
            Options: builder.Count == 0 ? null : builder.ConnectionString);
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.Options))
        {
            return details.FilePath?.Trim() ?? string.Empty;
        }

        var builder = ConnectionDetailKeys.TryCreateBuilder(details.Options)
            ?? new System.Data.Common.DbConnectionStringBuilder();
        ConnectionDetailKeys.Set(builder, ["Data Source", "DataSource"], details.FilePath?.Trim());
        return builder.ConnectionString;
    }
}

internal sealed class OracleDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "oracle",
        "Oracle",
        "Data Source=localhost:1521/FREEPDB1;User Id=app;Password=…");

    public DbConnection CreateConnection(string connectionString) =>
        new OracleConnection(connectionString);

    public string ListTablesSql => """
        SELECT table_name, 'table' FROM user_tables
        UNION ALL
        SELECT view_name, 'view' FROM user_views
        ORDER BY 1
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} FETCH FIRST {limit} ROWS ONLY";

    // Only the EZ Connect form "host[:port][/service]" can be tunneled; a TNS
    // alias resolves outside the connection string.
    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var source = new OracleConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith('('))
        {
            return null;
        }

        var slash = source.IndexOf('/', StringComparison.Ordinal);
        var address = slash < 0 ? source : source[..slash];
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? new DatabaseEndpoint(address.Trim(), 1521)
            : new DatabaseEndpoint(
                address[..colon].Trim(),
                int.TryParse(address[(colon + 1)..], out var port) ? port : 1521);
    }

    public string RewriteEndpoint(string connectionString, string host, int port)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        builder.DataSource = slash < 0
            ? $"{host}:{port}"
            : $"{host}:{port}{source[slash..]}";
        return builder.ConnectionString;
    }

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Data Source", "DataSource"],
        [],
        [],
        ["User Id", "UserID", "User"],
        ["Password"]);

    // The dialog's Database field carries the EZ Connect service name.
    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var details = DetailKeys.Parse(connectionString);
        if (details.Host is { } packed && !packed.StartsWith('('))
        {
            var slash = packed.IndexOf('/', StringComparison.Ordinal);
            var address = slash < 0 ? packed : packed[..slash];
            var colon = address.IndexOf(':', StringComparison.Ordinal);
            details = details with
            {
                Host = colon < 0 ? address.Trim() : address[..colon].Trim(),
                Port = colon >= 0 && int.TryParse(address[(colon + 1)..], out var port)
                    ? port
                    : null,
                Database = slash < 0 ? null : packed[(slash + 1)..].Trim(),
            };
        }

        return details;
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        var address = details.Port is { } port
            ? $"{details.Host}:{port}"
            : details.Host;
        var packed = string.IsNullOrWhiteSpace(details.Database)
            ? address
            : $"{address}/{details.Database}";
        return DetailKeys.Build(details with
        {
            Host = packed,
            Port = null,
            Database = null,
        });
    }
}

internal sealed class FirebirdDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "firebird",
        "Firebird",
        "DataSource=localhost;Database=/path/to/app.fdb;User=SYSDBA;Password=…");

    public DbConnection CreateConnection(string connectionString) =>
        new FbConnection(connectionString);

    public string ListTablesSql => """
        SELECT TRIM(rdb$relation_name),
               CASE WHEN rdb$view_blr IS NULL THEN 'table' ELSE 'view' END
        FROM rdb$relations
        WHERE COALESCE(rdb$system_flag, 0) = 0
        ORDER BY 1
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT FIRST {limit} * FROM {QuoteIdentifier(tableName)}";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new FbConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.DataSource)
            ? null
            : new DatabaseEndpoint(builder.DataSource, builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new FbConnectionStringBuilder(connectionString)
        {
            DataSource = host,
            Port = port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["DataSource", "Data Source", "Server", "Host"],
        ["Port", "Port Number"],
        ["Database", "Initial Catalog"],
        ["User", "User ID", "UserID"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

internal sealed class ClickHouseDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "clickhouse",
        "ClickHouse",
        "Host=localhost;Port=8123;Database=default;Username=default;Password=…");

    public DbConnection CreateConnection(string connectionString) =>
        new ClickHouseConnection(connectionString);

    public string ListTablesSql => """
        SELECT name,
               CASE WHEN engine = 'View' THEN 'view' ELSE 'table' END
        FROM system.tables
        WHERE database = currentDatabase()
        ORDER BY name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
        if (!builder.TryGetValue("Host", out var host)
            || string.IsNullOrWhiteSpace(host as string))
        {
            return null;
        }

        var port = builder.TryGetValue("Port", out var value)
            && int.TryParse(value as string, out var parsed)
                ? parsed
                : 8123;
        return new DatabaseEndpoint((string)host, port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
        builder["Host"] = host;
        builder["Port"] = port;
        return builder.ConnectionString;
    }

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Host"],
        ["Port"],
        ["Database"],
        ["Username", "User"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}
