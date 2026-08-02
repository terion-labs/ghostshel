using GhostShell.Application;
using GhostShell.Databases;

namespace GhostShell.Databases.Tests;

public sealed class DatabasePanelClientTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-databases-tests-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public void Built_in_drivers_have_unique_durable_ids()
    {
        var ids = BuiltInDatabaseDrivers.All
            .Select(driver => driver.Descriptor.Id)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
                "redshift", "duckdb", "oracle", "firebird", "clickhouse",
            ],
            ids);
        // The durable target format splits on the first colon, so ids must
        // never contain one — and pickers rely on non-blank display names.
        Assert.All(BuiltInDatabaseDrivers.All, driver =>
        {
            Assert.DoesNotContain(':', driver.Descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(driver.Descriptor.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(driver.Descriptor.ConnectionStringHint));
        });
    }

    [Fact]
    public async Task Lists_tables_and_views_and_pages_query_results()
    {
        var client = new DatabasePanelClient();
        _ = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            """
            CREATE TABLE people(id INTEGER PRIMARY KEY, name TEXT, joined TEXT);
            CREATE VIEW named_people AS SELECT name FROM people;
            """,
            maxRows: 10,
            CancellationToken.None);
        var inserted = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "INSERT INTO people(name, joined) VALUES ('Ada', '1843'), ('Grace', '1952'), (NULL, NULL);",
            maxRows: 10,
            CancellationToken.None);

        Assert.Empty(inserted.Columns);
        Assert.Equal(3, inserted.RowsAffected);

        var tables = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        Assert.Equal(
            [("named_people", DatabaseTableKind.View), ("people", DatabaseTableKind.Table)],
            tables.Select(table => (table.Name, table.Kind)));

        var page = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            client.BuildTablePreviewQuery("sqlite", "people", limit: 10),
            maxRows: 10,
            CancellationToken.None);
        Assert.Equal(["id", "name", "joined"], page.Columns.Select(column => column.Name));
        Assert.Equal(3, page.Rows.Count);
        Assert.False(page.Truncated);
        Assert.Equal("Ada", page.Rows[0][1]);
        // NULL stays null so the viewer can render it distinctly from "".
        Assert.Null(page.Rows[2][1]);

        var truncatedPage = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "SELECT * FROM people;",
            maxRows: 2,
            CancellationToken.None);
        Assert.Equal(2, truncatedPage.Rows.Count);
        Assert.True(truncatedPage.Truncated);
    }

    [Fact]
    public async Task Unknown_driver_is_rejected_before_any_connection_attempt()
    {
        var client = new DatabasePanelClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(
            "db2",
            "whatever",
            tunnel: null,
            CancellationToken.None));
        Assert.Throws<ArgumentException>(() =>
            client.BuildTablePreviewQuery("db2", "t", 10));
    }

    [Fact]
    public void Preview_queries_quote_hostile_table_names()
    {
        var client = new DatabasePanelClient();

        Assert.Equal(
            "SELECT * FROM \"weird\"\"name\" LIMIT 25;",
            client.BuildTablePreviewQuery("sqlite", "weird\"name", 25));
        Assert.Equal(
            "SELECT * FROM `weird``name` LIMIT 25;",
            client.BuildTablePreviewQuery("mysql", "weird`name", 25));
        Assert.Equal(
            "SELECT TOP (25) * FROM [weird]]name];",
            client.BuildTablePreviewQuery("sqlserver", "weird]name", 25));
        Assert.Equal(
            "SELECT * FROM \"t\" FETCH FIRST 25 ROWS ONLY",
            client.BuildTablePreviewQuery("oracle", "t", 25));
        Assert.Equal(
            "SELECT FIRST 25 * FROM \"t\"",
            client.BuildTablePreviewQuery("firebird", "t", 25));
    }
}
