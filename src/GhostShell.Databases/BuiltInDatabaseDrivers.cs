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
}

internal sealed class DuckDbDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "duckdb",
        "DuckDB",
        "Data Source=/path/to/analytics.duckdb");

    public DbConnection CreateConnection(string connectionString) =>
        new DuckDBConnection(connectionString);

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
}
