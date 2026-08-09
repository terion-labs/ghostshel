using GhostShell.Application;
using Xunit.Abstractions;

namespace GhostShell.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Providers =>
        DatabaseProviderSelection.SelectedProviderIds();

    [DatabaseIntegrationTheory]
    [MemberData(nameof(Providers))]
    public async Task Every_database_viewer_operation_conforms(string providerId)
    {
        var provider = DatabaseProviderCatalog.Get(providerId);
        output.WriteLine($"Starting {provider.DisplayName} ({provider.Id})...");
        if (provider.Expectations.CompatibilityNote is { } note)
        {
            output.WriteLine($"Compatibility scope: {note}");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await using var environment = await provider.StartAsync(timeout.Token);
        await using var client = new DatabasePanelClient();

        await WaitUntilReadyAsync(client, environment, timeout.Token);
        await SeedAsync(client, environment, timeout.Token);
        var objects = await AssertDriverAndCatalogAsync(client, environment, timeout.Token);
        await AssertSchemaDiagramAsync(client, environment, objects, timeout.Token);
        await AssertTypedBrowsingAsync(client, environment, objects, timeout.Token);
        await AssertMutationsAsync(client, environment, objects, timeout.Token);
        await AssertViewModelJourneyAsync(client, environment, objects, timeout.Token);
        await AssertHeadlessViewJourneyAsync(
            client,
            environment,
            objects,
            timeout.Token);

        await AssertQueryFailureRecoveryAsync(client, environment, timeout.Token);

        output.WriteLine($"{provider.DisplayName}: complete conformance workflow passed.");
    }

    private static async Task AssertSchemaDiagramAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var graph = await client.GetDatabaseSchemaGraphAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);

        Assert.NotEmpty(graph.Tables);
        Assert.DoesNotContain(graph.Tables, table =>
            table.Object.Kind == DatabaseTableKind.View);
        var rows = Assert.Single(graph.Tables, table =>
            table.Object.Id == objects.Rows.Id);
        Assert.Equal(
            objects.Rows.Name,
            rows.Object.Name);
        Assert.Contains(rows.Columns, column => column.Name == "id");
        Assert.Contains(rows.Columns, column => column.Name == "title");

        if (DiagramRelationshipSeed(environment.Provider.Id).Count > 0)
        {
            var child = Assert.Single(graph.Tables, table =>
                table.Object.Name == "viewer_er_child");
            var relationship = Assert.Single(child.ForeignKeys);
            Assert.False(string.IsNullOrWhiteSpace(relationship.Name));
            Assert.Equal("viewer_er_parent", relationship.ReferencedObject.Name);
            var pair = Assert.Single(relationship.Columns);
            Assert.Equal("parent_id", pair.ColumnName);
            Assert.Equal("id", pair.ReferencedColumnName);
        }

        var mermaid = DatabaseMermaidErDiagram.Create(graph);
        Assert.StartsWith("```mermaid", mermaid, StringComparison.Ordinal);
        Assert.Contains(objects.Rows.DisplayName, mermaid, StringComparison.Ordinal);
        if (DiagramRelationshipSeed(environment.Provider.Id).Count > 0)
        {
            Assert.Contains("viewer_er_parent", mermaid, StringComparison.Ordinal);
            Assert.Contains("viewer_er_child", mermaid, StringComparison.Ordinal);
        }
        Assert.EndsWith("```\n", mermaid, StringComparison.Ordinal);
    }

    private static async Task WaitUntilReadyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await client.QueryAsync(
                    environment.Provider.Id,
                    environment.ConnectionString,
                    tunnel: null,
                    environment.Provider.ReadySql,
                    maxRows: 1,
                    cancellationToken);
                return;
            }
            catch (Exception exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{environment.Provider.DisplayName} opened its port but never accepted the production driver connection.",
            lastFailure);
    }

    private static async Task SeedAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        foreach (var statement in environment.Provider.Seed.Statements)
        {
            _ = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                statement,
                maxRows: 1,
                cancellationToken);
        }

        foreach (var statement in DiagramRelationshipSeed(environment.Provider.Id))
        {
            _ = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                statement,
                maxRows: 1,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> DiagramRelationshipSeed(string providerId) =>
        providerId switch
        {
            "sqlite" or "duckdb" or "postgres" or "cockroach" =>
            [
                "CREATE TABLE viewer_er_parent (id INTEGER PRIMARY KEY)",
                "CREATE TABLE viewer_er_child (id INTEGER PRIMARY KEY, parent_id INTEGER, "
                    + "CONSTRAINT fk_viewer_er_parent FOREIGN KEY (parent_id) "
                    + "REFERENCES viewer_er_parent (id))",
            ],
            "mysql" or "mariadb" =>
            [
                "CREATE TABLE viewer_er_parent (id INT PRIMARY KEY) ENGINE=InnoDB",
                "CREATE TABLE viewer_er_child (id INT PRIMARY KEY, parent_id INT, "
                    + "CONSTRAINT fk_viewer_er_parent FOREIGN KEY (parent_id) "
                    + "REFERENCES viewer_er_parent (id)) ENGINE=InnoDB",
            ],
            "sqlserver" =>
            [
                "CREATE TABLE [viewer_er_parent] ([id] INT PRIMARY KEY)",
                "CREATE TABLE [viewer_er_child] ([id] INT PRIMARY KEY, [parent_id] INT, "
                    + "CONSTRAINT [fk_viewer_er_parent] FOREIGN KEY ([parent_id]) "
                    + "REFERENCES [viewer_er_parent] ([id]))",
            ],
            "oracle" or "firebird" =>
            [
                "CREATE TABLE \"viewer_er_parent\" (\"id\" INTEGER PRIMARY KEY)",
                "CREATE TABLE \"viewer_er_child\" (\"id\" INTEGER PRIMARY KEY, \"parent_id\" INTEGER, "
                    + "CONSTRAINT \"fk_viewer_er_parent\" FOREIGN KEY (\"parent_id\") "
                    + "REFERENCES \"viewer_er_parent\" (\"id\"))",
            ],
            _ => [],
        };

    private static async Task<DatabaseObjects> AssertDriverAndCatalogAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var provider = environment.Provider;
        var driver = Assert.Single(client.Drivers, candidate => candidate.Id == provider.Id);
        Assert.Equal(provider.Id is "sqlite" or "duckdb", driver.IsFileBased);

        var connectionDetails = client.ParseConnectionDetails(
            provider.Id,
            environment.ConnectionString);
        var rebuiltConnectionString = client.BuildConnectionString(provider.Id, connectionDetails);
        var tables = await client.ListTablesAsync(
            provider.Id,
            rebuiltConnectionString,
            tunnel: null,
            cancellationToken);

        var rows = FindObject(tables, provider.Seed.RowsTable, DatabaseTableKind.Table);
        var keyless = FindObject(tables, provider.Seed.KeylessTable, provider.Seed.KeylessKind);
        var view = FindObject(tables, provider.Seed.View, DatabaseTableKind.View);
        var hostile = FindObject(tables, provider.Seed.HostileTable, DatabaseTableKind.Table);

        var rowsDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            rows,
            cancellationToken);
        var id = Assert.Single(rowsDetails.Columns, column => column.Name == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.Equal(1, id.PrimaryKeyOrdinal);
        Assert.Equal(provider.Expectations.HasIdentity, id.IsIdentity);
        var code = Assert.Single(rowsDetails.Columns, column => column.Name == "code");
        var score = Assert.Single(rowsDetails.Columns, column => column.Name == "score");
        var noteColumn = Assert.Single(rowsDetails.Columns, column => column.Name == "note");
        var status = Assert.Single(rowsDetails.Columns, column => column.Name == "status");
        Assert.True(noteColumn.IsNullable);
        Assert.False(status.IsNullable);
        Assert.NotNull(status.DefaultExpression);
        Assert.Contains(
            "draft",
            status.DefaultExpression,
            StringComparison.OrdinalIgnoreCase);
        if (provider.Expectations.ExpectedCodeLength is { } codeLength)
        {
            Assert.Equal(codeLength, code.Length);
        }

        if (provider.Expectations.ExpectedScorePrecision is { } scorePrecision)
        {
            Assert.Equal(scorePrecision, score.Precision);
        }

        if (provider.Expectations.ExpectedScoreScale is { } scoreScale)
        {
            Assert.Equal(scoreScale, score.Scale);
        }

        var generated = Assert.Single(
            rowsDetails.Columns,
            column => column.Name == "computed_label");
        Assert.Equal(provider.Expectations.HasGeneratedColumn, generated.IsGenerated);
        Assert.Equal(provider.Expectations.HasGeneratedColumn, generated.IsReadOnly);
        if (provider.Expectations.HasGeneratedColumn)
        {
            Assert.False(generated.CanEdit);
        }

        Assert.Equal(provider.Expectations.CanEdit, rowsDetails.CanEdit);
        if (provider.Expectations.HasIndexes)
        {
            var scoreIndex = Assert.Single(
                rowsDetails.Indexes,
                index => index.Name == "idx_viewer_rows_score");
            Assert.NotNull(provider.Expectations.ScoreIndex);
            AssertScoreIndex(scoreIndex, provider.Expectations.ScoreIndex);
        }
        else
        {
            Assert.Empty(rowsDetails.Indexes);
        }

        var viewDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            view,
            cancellationToken);
        Assert.False(viewDetails.CanEdit);
        Assert.NotEmpty(viewDetails.Columns);
        Assert.Contains("read-only", viewDetails.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);

        var keylessDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            keyless,
            cancellationToken);
        Assert.False(keylessDetails.CanEdit);
        Assert.NotEmpty(keylessDetails.Columns);
        Assert.False(string.IsNullOrWhiteSpace(keylessDetails.ReadOnlyReason));
        if (provider.Expectations.CanEdit && keyless.Kind == DatabaseTableKind.Table)
        {
            Assert.Contains(
                "primary key",
                keylessDetails.ReadOnlyReason,
                StringComparison.OrdinalIgnoreCase);
        }

        var qualifiedPreview = client.BuildTablePreviewQuery(provider.Id, rows.Id, limit: 3);
        var preview = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            qualifiedPreview,
            maxRows: 2,
            cancellationToken);
        Assert.Equal(2, preview.ValueRows.Count);
        Assert.True(preview.Truncated);

        var legacyPreview = client.BuildTablePreviewQuery(provider.Id, rows.Name, limit: 1);
        var legacyPage = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            legacyPreview,
            maxRows: 1,
            cancellationToken);
        Assert.Single(legacyPage.ValueRows);

        var hostilePreview = client.BuildTablePreviewQuery(provider.Id, hostile.Id, limit: 1);
        var hostilePage = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            hostilePreview,
            maxRows: 1,
            cancellationToken);
        Assert.Equal("42", Assert.Single(Assert.Single(hostilePage.ValueRows)).DisplayText);

        return new DatabaseObjects(rows, rowsDetails, keyless, view, hostile);
    }

    private static void AssertScoreIndex(
        DatabaseIndexSchema index,
        DatabaseIndexExpectations expectations)
    {
        Assert.False(index.IsUnique);
        Assert.False(index.IsPrimary);
        Assert.True(index.IsValid);
        var firstColumn = Assert.Single(
            index.Columns,
            column => column.Ordinal == index.Columns.Min(candidate => candidate.Ordinal));
        Assert.Equal(expectations.FirstColumnDescending, firstColumn.IsDescending);
        Assert.Contains(
            "score",
            firstColumn.Name ?? firstColumn.Expression ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var includedColumns = index.Columns.Where(column => column.IsIncluded).ToArray();
        if (expectations.IncludedColumn is { } includedColumn)
        {
            var included = Assert.Single(includedColumns);
            Assert.Equal(includedColumn, included.Name, ignoreCase: true);
        }
        else
        {
            Assert.Empty(includedColumns);
        }

        if (expectations.PredicateFragment is { } predicateFragment)
        {
            var predicate = index.Predicate
                ?? index.Details?.GetValueOrDefault("Definition")
                ?? string.Empty;
            Assert.Contains(predicateFragment, predicate, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertQueryFailureRecoveryAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            "SELECT * FROM ghostshell_table_that_does_not_exist",
            maxRows: 1,
            cancellationToken));

        var recovered = await client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            environment.Provider.ReadySql,
            maxRows: 1,
            cancellationToken);
        Assert.Single(recovered.ValueRows);
    }

    private static DatabaseTableDescriptor FindObject(
        IReadOnlyList<DatabaseTableDescriptor> objects,
        string name,
        DatabaseTableKind kind) =>
        Assert.Single(objects, databaseObject =>
            databaseObject.Name == name && databaseObject.Kind == kind);

    private sealed record DatabaseObjects(
        DatabaseTableDescriptor Rows,
        DatabaseObjectDetails RowsDetails,
        DatabaseTableDescriptor Keyless,
        DatabaseTableDescriptor View,
        DatabaseTableDescriptor Hostile);
}
