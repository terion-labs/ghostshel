using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DatabaseRuntimePanelViewModelTests
{
    [Fact]
    public async Task Connect_lists_tables_and_publishes_the_durable_target()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client);

        Assert.Equal(PanelKind.DatabaseViewer, panel.Kind);
        Assert.False(panel.IsConnected);
        Assert.Null(panel.RecoveryTarget);

        panel.ConnectionString = "Data Source=demo.db";
        await panel.ConnectAsync();

        Assert.True(panel.IsConnected);
        Assert.Equal(["people", "names"], panel.Tables.Select(table => table.Name));
        Assert.Equal("sqlite:Data Source=demo.db", panel.RecoveryTarget);

        // Editing the target drops the connected state until re-probed.
        panel.ConnectionString = "Data Source=other.db";
        Assert.False(panel.IsConnected);
    }

    [Fact]
    public async Task Restored_panel_reconnects_from_its_saved_target()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");

        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.Equal("sqlite", panel.SelectedDriver.Id);
        Assert.Equal(2, panel.Tables.Count);
    }

    [Fact]
    public async Task Query_results_render_null_cells_and_the_summary()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        panel.QueryText = "SELECT id, name FROM people;";
        await panel.RunQueryAsync();

        Assert.True(panel.HasResults);
        Assert.Equal(["id", "name"], panel.ResultColumns.Select(column => column.Name));
        var lastRow = panel.ResultRows[^1];
        Assert.True(lastRow.Cells[1].IsNull);
        Assert.Equal("NULL", lastRow.Cells[1].Text);
        Assert.StartsWith("2 rows", panel.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("SELECT id, name FROM people;", client.LastSql);
    }

    [Fact]
    public async Task Failures_surface_inline_and_clear_on_the_next_operation()
    {
        var client = new FakeDatabasePanelClient { FailWith = "no such table: missing" };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client);
        panel.ConnectionString = "Data Source=demo.db";

        await panel.ConnectAsync();

        Assert.False(panel.IsConnected);
        Assert.True(panel.HasError);
        Assert.Equal("no such table: missing", panel.ErrorMessage);

        client.FailWith = null;
        await panel.ConnectAsync();

        Assert.True(panel.IsConnected);
        Assert.False(panel.HasError);
    }

    [Fact]
    public async Task Table_preview_fills_the_editor_with_the_driver_query()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        await panel.PreviewTableAsync(panel.Tables[0]);

        Assert.Equal("SELECT * FROM \"people\" LIMIT 200;", panel.QueryText);
        Assert.Equal(panel.QueryText, client.LastSql);
        Assert.True(panel.HasResults);
    }

    private sealed class FakeDatabasePanelClient : IDatabasePanelClient
    {
        public string? FailWith { get; set; }

        public string? LastSql { get; private set; }

        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("sqlite", "SQLite", "Data Source=…"),
            new("postgres", "PostgreSQL", "Host=…"),
        ];

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            return Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
            [
                new("people", DatabaseTableKind.Table),
                new("names", DatabaseTableKind.View),
            ]);
        }

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            string sql,
            int maxRows,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            LastSql = sql;
            return Task.FromResult(new DatabaseQueryPage(
                [new("id", "INTEGER"), new("name", "TEXT")],
                [
                    new string?[] { "1", "Ada" },
                    new string?[] { "2", null },
                ],
                Truncated: false,
                RowsAffected: 0,
                TimeSpan.FromMilliseconds(3)));
        }

        public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
            $"SELECT * FROM \"{tableName}\" LIMIT {limit};";

        private void ThrowIfConfigured()
        {
            if (FailWith is { } message)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
