using System.Data.Common;
using GhostShell.Application;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace GhostShell.Databases;

/// <summary>The drivers this build ships. Ids are durable: saved panels store them.</summary>
public static class BuiltInDatabaseDrivers
{
    public static IReadOnlyList<IDatabaseDriver> All { get; } =
    [
        new SqliteDatabaseDriver(),
        new PostgresDatabaseDriver(),
        new MySqlDatabaseDriver(),
    ];
}

internal sealed class SqliteDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "sqlite",
        "SQLite",
        "Data Source=/path/to/database.db");

    public DbConnection CreateConnection(string connectionString) =>
        new SqliteConnection(connectionString);

    public string ListTablesSql => """
        SELECT name, type FROM sqlite_master
        WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
        ORDER BY name;
        """;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";
}

internal sealed class PostgresDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "postgres",
        "PostgreSQL",
        "Host=localhost;Port=5432;Database=app;Username=postgres;Password=…");

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
}

internal sealed class MySqlDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "mysql",
        "MySQL",
        "Server=localhost;Port=3306;Database=app;User ID=root;Password=…");

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
}
