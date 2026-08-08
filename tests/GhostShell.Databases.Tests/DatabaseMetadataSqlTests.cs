using GhostShell.Application;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseMetadataSqlTests
{
    private static readonly DatabaseObjectId ObjectId = new("catalog", "MixedSchema", "MixedTable");

    [Fact]
    public void Firebird_computed_columns_use_the_fields_catalog_alias()
    {
        var sql = Reader("firebird").BuildColumnsSql(ObjectId);

        Assert.Contains("f.rdb$computed_source", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("rf.rdb$computed_source", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Firebird_classifies_views_by_relation_type()
    {
        var driver = Assert.Single(
            BuiltInDatabaseDrivers.All,
            candidate => candidate.Descriptor.Id == "firebird");

        Assert.Contains("rdb$relation_type = 1", driver.ListTablesSql, StringComparison.Ordinal);
        Assert.Contains("AS VARCHAR(5)", driver.ListTablesSql, StringComparison.Ordinal);
        Assert.DoesNotContain("rdb$view_blr", driver.ListTablesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sqlite_identity_requires_a_single_integer_rowid_primary_key()
    {
        var sql = Reader("sqlite").BuildColumnsSql(ObjectId);

        Assert.Contains("count(*) FROM column_info WHERE pk > 0", sql, StringComparison.Ordinal);
        Assert.Contains("pragma_table_list(@table)", sql, StringComparison.Ordinal);
        Assert.Contains("max(wr)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_reads_identity_and_generated_flags_without_expanding_redshift_sql()
    {
        var postgreSql = Reader("postgres").BuildColumnsSql(ObjectId);
        var redshift = Reader("redshift").BuildColumnsSql(ObjectId);

        Assert.Contains("c.is_identity = 'YES'", postgreSql, StringComparison.Ordinal);
        Assert.Contains("c.is_generated = 'ALWAYS'", postgreSql, StringComparison.Ordinal);
        Assert.DoesNotContain("c.is_identity", redshift, StringComparison.Ordinal);
        Assert.DoesNotContain("c.is_generated", redshift, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_casts_index_ordinality_for_pg_get_indexdef()
    {
        var sql = Reader("postgres").BuildIndexesSql(ObjectId);

        Assert.Contains(
            "pg_get_indexdef(ix.indexrelid, keys.ordinality::integer, true)",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_structure_and_indexes_cover_tables_and_views()
    {
        var reader = Reader("sqlserver");
        var columns = reader.BuildColumnsSql(ObjectId);
        var indexes = reader.BuildIndexesSql(ObjectId);

        Assert.Contains("FROM sys.objects o", columns, StringComparison.Ordinal);
        Assert.Contains("o.type IN ('U', 'V')", columns, StringComparison.Ordinal);
        Assert.Contains("c.generated_always_type", columns, StringComparison.Ordinal);
        Assert.Contains("c.system_type_id IN (231, 239)", columns, StringComparison.Ordinal);
        Assert.Contains("FROM sys.objects o", indexes, StringComparison.Ordinal);
        Assert.Contains("o.type IN ('U', 'V')", indexes, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_metadata_preserves_exact_catalog_case()
    {
        var reader = Reader("oracle");
        var columns = reader.BuildColumnsSql(ObjectId);
        var indexes = reader.BuildIndexesSql(ObjectId);

        Assert.Contains("c.owner = :schema", columns, StringComparison.Ordinal);
        Assert.Contains("c.table_name = :object_name", columns, StringComparison.Ordinal);
        Assert.Contains("FROM all_tab_cols c", columns, StringComparison.Ordinal);
        Assert.Contains("c.hidden_column = 'NO'", columns, StringComparison.Ordinal);
        Assert.Contains("i.table_owner = :schema", indexes, StringComparison.Ordinal);
        Assert.Contains("i.table_name = :object_name", indexes, StringComparison.Ordinal);
        Assert.DoesNotContain(":table", columns, StringComparison.Ordinal);
        Assert.DoesNotContain(":table", indexes, StringComparison.Ordinal);
        Assert.DoesNotContain("upper(", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upper(", indexes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySql_generated_columns_and_transactional_edit_gate_are_conservative()
    {
        var reader = Reader("mysql");
        var columns = reader.BuildColumnsSql(ObjectId);
        var capability = reader.BuildMutationCapabilitySql(ObjectId);

        Assert.Contains("c.generation_expression", columns, StringComparison.Ordinal);
        Assert.Contains("SELECT engine", capability, StringComparison.Ordinal);
        Assert.True(DatabaseMetadataReader.IsTransactionalMySqlEngine("InnoDB"));
        Assert.True(DatabaseMetadataReader.IsTransactionalMySqlEngine("innodb"));
        Assert.False(DatabaseMetadataReader.IsTransactionalMySqlEngine("MyISAM"));
        Assert.False(DatabaseMetadataReader.IsTransactionalMySqlEngine(null));
    }

    [Fact]
    public void ClickHouse_uses_the_provider_supported_parameter_markers()
    {
        var columns = Reader("clickhouse").BuildColumnsSql(ObjectId);

        // ClickHouse.Client 7.8 rewrites @name to a typed {name:Type}
        // server parameter, so retaining @ here is both typed and injectable-safe.
        Assert.Contains("database = @schema AND table = @table", columns, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickHouse_index_union_normalizes_nullable_text_columns()
    {
        var indexes = Reader("clickhouse").BuildIndexesSql(ObjectId);

        Assert.Contains("CAST(NULL AS Nullable(String))", indexes, StringComparison.Ordinal);
        Assert.Contains("CAST(expr AS Nullable(String))", indexes, StringComparison.Ordinal);
        Assert.Contains("CAST(primary_key AS Nullable(String))", indexes, StringComparison.Ordinal);
        Assert.Contains("CAST(sorting_key AS Nullable(String))", indexes, StringComparison.Ordinal);
        Assert.Contains("CAST(create_table_query AS Nullable(String))", indexes, StringComparison.Ordinal);
    }

    private static DatabaseMetadataReader Reader(string driverId) =>
        new(DatabaseSqlDialect.For(driverId));
}
