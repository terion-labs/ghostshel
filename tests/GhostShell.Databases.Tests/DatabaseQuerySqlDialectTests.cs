using DuckDB.NET.Data;
using GhostShell.Application;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseQuerySqlDialectTests
{
    private const string HostileFilter = "x%' OR 1=1; DROP TABLE source_rows;--";

    [Theory]
    [InlineData("sqlite", "@p0")]
    [InlineData("postgres", "@p0")]
    [InlineData("cockroach", "@p0")]
    [InlineData("redshift", "@p0")]
    [InlineData("mysql", "@p0")]
    [InlineData("mariadb", "@p0")]
    [InlineData("sqlserver", "@p0")]
    [InlineData("duckdb", "$p0")]
    [InlineData("oracle", ":p0")]
    [InlineData("firebird", "@p0")]
    [InlineData("clickhouse", "@p0")]
    public void Every_driver_counts_the_filtered_raw_result_without_paging_or_interpolation(
        string driverId,
        string marker)
    {
        const string sourceSql = "SELECT raw_name, raw_score FROM source_rows;";
        var command = DatabaseSqlDialect.For(driverId).BuildQueryCount(
            sourceSql,
            [new DatabaseColumnDescriptor("raw_name", "TEXT", DatabaseValueKind.Text)],
            [new DatabaseFilterCondition(
                "raw_name",
                DatabaseFilterOperator.Equal,
                HostileFilter)]);

        Assert.Contains("SELECT COUNT(*)", command.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT raw_name, raw_score FROM source_rows", command.Sql, StringComparison.Ordinal);
        Assert.Contains($" = {marker}", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(HostileFilter, command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" OFFSET ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" FETCH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(driverId, "duckdb", StringComparison.Ordinal))
        {
            Assert.StartsWith(
                "WITH \"__ghostshell_query\" AS MATERIALIZED",
                command.Sql,
                StringComparison.Ordinal);
        }

        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("p0", parameter.Name);
        Assert.Equal(HostileFilter, parameter.Value);
    }

    [Fact]
    public void SqlServer_makes_a_top_level_order_by_legal_inside_the_browsing_wrapper()
    {
        var dialect = DatabaseSqlDialect.For("sqlserver");
        var columns =
            new[] { new DatabaseColumnDescriptor("id", "BIGINT", DatabaseValueKind.SignedInteger) };
        const string source = "SELECT id FROM source_rows WHERE note = 'ORDER BY' ORDER BY id;";

        var page = dialect.BuildQuerySelect(
            source,
            columns,
            DatabaseTableQuery.FirstPage(25));
        var count = dialect.BuildQueryCount(source, columns, []);

        Assert.Contains("ORDER BY id\nOFFSET 0 ROWS", page.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY id\nOFFSET 0 ROWS", count.Sql, StringComparison.Ordinal);
        Assert.Contains("AS [__ghostshell_query] ([id])", page.Sql, StringComparison.Ordinal);
        Assert.Contains("AS [__ghostshell_query] ([id])", count.Sql, StringComparison.Ordinal);

        var top = dialect.BuildQueryCount(
            "SELECT TOP 10 id FROM source_rows ORDER BY id;",
            columns,
            []);
        Assert.DoesNotContain("ORDER BY id\nOFFSET", top.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "sqlite",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "postgres",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "cockroach",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "redshift",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "mysql",
        "AS `__ghostshell_query`",
        "`odd\"name`",
        "`sort]score`",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "mariadb",
        "AS `__ghostshell_query`",
        "`odd\"name`",
        "`sort]score`",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "sqlserver",
        "AS [__ghostshell_query]",
        "[odd\"name]",
        "[sort]]score]",
        "@p0",
        "OFFSET 3 ROWS FETCH NEXT 7 ROWS ONLY;")]
    [InlineData(
        "duckdb",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "$p0",
        "LIMIT 7 OFFSET 3;")]
    [InlineData(
        "oracle",
        "\"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        ":p0",
        "OFFSET 3 ROWS FETCH NEXT 7 ROWS ONLY;")]
    [InlineData(
        "firebird",
        "AS \"__ghostshell_query\"",
        "\"odd\"\"name\"",
        "\"sort]score\"",
        "@p0",
        "ROWS 4 TO 10;")]
    [InlineData(
        "clickhouse",
        "AS `__ghostshell_query`",
        "`odd\"name`",
        "`sort]score`",
        "@p0",
        "LIMIT 7 OFFSET 3;")]
    public void Every_driver_wraps_query_results_with_its_own_identifiers_parameters_and_paging(
        string driverId,
        string expectedAlias,
        string expectedFilterColumn,
        string expectedSortColumn,
        string expectedMarker,
        string expectedPageClause)
    {
        var dialect = DatabaseSqlDialect.For(driverId);
        var sourceSql = "SELECT raw_name, raw_score\nFROM source_rows;\r\n\t";
        const string sourceBody = "SELECT raw_name, raw_score\nFROM source_rows";
        var columns = new[]
        {
            new DatabaseColumnDescriptor("odd\"name", "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnDescriptor("sort]score", "INTEGER", DatabaseValueKind.SignedInteger),
        };

        var command = dialect.BuildQuerySelect(
            sourceSql,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "odd\"name",
                    DatabaseFilterOperator.Equal,
                    HostileFilter)],
                [new DatabaseSort("sort]score", Descending: true)],
                Offset: 3,
                Limit: 7));

        Assert.Equal("SELECT raw_name, raw_score\nFROM source_rows;\r\n\t", sourceSql);
        var queryAlias = expectedAlias.StartsWith("AS ", StringComparison.Ordinal)
            ? expectedAlias[3..]
            : expectedAlias;
        var expectedProjection = $"{queryAlias}.{expectedFilterColumn}, "
            + $"{queryAlias}.{expectedSortColumn}";
        if (string.Equals(driverId, "duckdb", StringComparison.Ordinal))
        {
            Assert.StartsWith(
                $"WITH \"__ghostshell_query\" AS MATERIALIZED (\n{sourceBody}\n)\n"
                + $"SELECT {expectedProjection} FROM \"__ghostshell_query\"",
                command.Sql);
        }
        else
        {
            Assert.StartsWith(
                $"SELECT {expectedProjection} FROM ({sourceBody}\n) {expectedAlias}",
                command.Sql);
        }
        Assert.Contains(
            $"WHERE {expectedFilterColumn} = {expectedMarker}",
            command.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"ORDER BY {expectedSortColumn} DESC",
            command.Sql,
            StringComparison.Ordinal);
        Assert.EndsWith(expectedPageClause, command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(HostileFilter, command.Sql, StringComparison.Ordinal);

        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("p0", parameter.Name);
        Assert.Equal(HostileFilter, parameter.Value);
    }

    [Fact]
    public void DuckDb_executes_a_sorted_page_over_an_expression_query_wrapping_a_preview_page()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                CREATE TABLE viewer_rows (id BIGINT PRIMARY KEY, code VARCHAR, score DECIMAL(12, 2));
                INSERT INTO viewer_rows VALUES (1, 'alpha', -100), (2, 'beta', 0), (3, 'omega', 300);
                """;
            seed.ExecuteNonQuery();
        }

        const string sourceSql = """
            SELECT "ghostshell_base".*, "ghostshell_base"."score" + 1 AS "ghostshell_expression"
            FROM (SELECT * FROM "main"."viewer_rows" LIMIT 500 OFFSET 0) AS "ghostshell_base"
            """;
        var dialect = DatabaseSqlDialect.For("duckdb");
        var command = dialect.BuildQuerySelect(
            sourceSql,
            [
                new DatabaseColumnDescriptor("id", "BIGINT", DatabaseValueKind.SignedInteger, IsKey: true),
                new DatabaseColumnDescriptor("code", "VARCHAR", DatabaseValueKind.Text),
                new DatabaseColumnDescriptor("score", "DECIMAL(12,2)", DatabaseValueKind.Decimal),
                new DatabaseColumnDescriptor("ghostshell_expression", "DECIMAL(13,2)", DatabaseValueKind.Decimal),
            ],
            new DatabaseTableQuery([], [new DatabaseSort("id", Descending: true)], Offset: 0, Limit: 201));

        Assert.Equal(
            """
            WITH "__ghostshell_query" AS MATERIALIZED (
            SELECT "ghostshell_base".*, "ghostshell_base"."score" + 1 AS "ghostshell_expression"
            FROM (SELECT * FROM "main"."viewer_rows" LIMIT 500 OFFSET 0) AS "ghostshell_base"
            )
            SELECT "__ghostshell_query"."id", "__ghostshell_query"."code", "__ghostshell_query"."score", "__ghostshell_query"."ghostshell_expression" FROM "__ghostshell_query" ORDER BY "id" DESC LIMIT 201 OFFSET 0;
            """,
            command.Sql);

        using var query = connection.CreateCommand();
        query.CommandText = command.Sql;
        using var reader = query.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        Assert.Equal([3L, 2L, 1L], ids);
    }
}
