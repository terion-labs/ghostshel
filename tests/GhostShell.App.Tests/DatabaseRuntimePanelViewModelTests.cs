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
    public async Task Selecting_a_row_fills_the_field_inspector_and_toggles_off()
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

        Assert.Equal([1, 2], panel.ResultRows.Select(row => row.Number));
        Assert.False(panel.HasSelectedRow);

        panel.SelectRow(panel.ResultRows[1]);

        Assert.True(panel.HasSelectedRow);
        Assert.True(panel.ResultRows[1].IsSelected);
        Assert.Equal(
            [("id", "2", false), ("name", "NULL", true)],
            panel.SelectedRowFields.Select(field => (field.Name, field.Text, field.IsNull)));

        // Selecting the same row again clears the inspector; a fresh result
        // set also starts unselected.
        panel.SelectRow(panel.ResultRows[1]);
        Assert.False(panel.HasSelectedRow);
        Assert.False(panel.ResultRows[1].IsSelected);

        panel.SelectRow(panel.ResultRows[0]);
        await panel.RunQueryAsync();
        Assert.False(panel.HasSelectedRow);
    }

    [Fact]
    public async Task Switching_the_tunnel_reconnects_through_the_new_route()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        Assert.Equal("Direct", panel.ConnectionDisplayName);
        Assert.Null(client.LastTunnel);

        var bastion = new ConnectionProfile(
            new ConnectionId("bastion"),
            ConnectionProfile.CurrentSchemaVersion,
            "bastion-eu",
            new ConnectionEndpoint.Ssh("bastion.example.test", username: "ops"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        panel.SetTunnel(bastion);
        await panel.Initialization;
        // SetTunnel re-probes asynchronously; wait for the busy window to close.
        while (panel.IsBusy)
        {
            await Task.Yield();
        }

        Assert.Equal("bastion-eu", panel.ConnectionDisplayName);
        Assert.Equal(bastion.Id, panel.TunnelConnectionId);
        Assert.True(panel.IsConnected);
        Assert.Same(bastion, client.LastTunnel);

        // A local connection means direct again.
        var local = new ConnectionProfile(
            new ConnectionId("local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        panel.SetTunnel(local);
        while (panel.IsBusy)
        {
            await Task.Yield();
        }

        Assert.Equal("Direct", panel.ConnectionDisplayName);
        Assert.Null(panel.TunnelConnectionId);
        Assert.Null(client.LastTunnel);
    }

    [Fact]
    public async Task Saved_connection_shows_its_name_and_injects_the_vault_password()
    {
        var client = new FakeDatabasePanelClient();
        var secret = SecretRef.New();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app",
            secret);
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordResolver: (reference, _) => Task.FromResult<string?>(
                reference == secret ? "vaulted" : null));
        await panel.Initialization;

        Assert.True(panel.IsSavedConnection);
        Assert.Equal("prod-core", panel.AddressBarText);
        Assert.Equal("postgres", panel.SelectedDriver.Id);
        Assert.Equal($"saved:{profile.Id.Value}", panel.RecoveryTarget);
        Assert.True(panel.IsConnected);
        Assert.Equal(
            "Host=db.internal;Database=app;Password=vaulted",
            client.LastConnectionString);
    }

    [Fact]
    public async Task Saved_connection_without_password_asks_before_connecting()
    {
        var client = new FakeDatabasePanelClient();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app");
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile);
        var prompts = 0;
        panel.PasswordRequested += (_, _) => prompts++;
        await panel.Initialization;

        // Construction must not connect: the prompt needs a view first.
        Assert.False(panel.IsConnected);

        await panel.ConnectAsync();
        Assert.Equal(1, prompts);
        Assert.False(panel.IsConnected);

        panel.SetSessionPassword("typed");
        await panel.ConnectAsync();
        Assert.True(panel.IsConnected);
        Assert.Equal(
            "Host=db.internal;Database=app;Password=typed",
            client.LastConnectionString);
    }

    [Fact]
    public async Task Editing_details_detaches_the_panel_from_the_saved_connection()
    {
        var client = new FakeDatabasePanelClient();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app",
            SecretRef.New());
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordResolver: (_, _) => Task.FromResult<string?>("vaulted"));
        await panel.Initialization;
        Assert.True(panel.IsSavedConnection);

        await panel.ApplyConnectionDetailsAsync(
            new DatabaseConnectionDetails(Options: "Host=other;Database=app"));

        Assert.False(panel.IsSavedConnection);
        Assert.StartsWith("postgres:", panel.RecoveryTarget, StringComparison.Ordinal);
        Assert.Equal("Host=other;Database=app", client.LastConnectionString);
    }

    [Fact]
    public async Task Copy_builders_render_json_csv_and_sql_insert()
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
        var row = panel.ResultRows[^1];

        var json = panel.BuildRowJson(row);
        Assert.Contains("\"id\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": null", json, StringComparison.Ordinal);

        Assert.Equal(
            "id,name" + Environment.NewLine + "2,",
            panel.BuildRowCsv(row));

        Assert.Equal(
            "INSERT INTO \"people\" (\"id\", \"name\") VALUES (2, NULL);",
            panel.BuildRowSqlInsert(row));

        var first = panel.ResultRows[0];
        Assert.Equal(
            "INSERT INTO \"people\" (\"id\", \"name\") VALUES (1, 'Ada');",
            panel.BuildRowSqlInsert(first));
    }

    [Fact]
    public void Connection_string_display_masks_password_values()
    {
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            new FakeDatabasePanelClient());

        panel.ConnectionString = "Host=db;Username=ops;Password=s3cret;SSL Mode=Require";
        Assert.Equal(
            "Host=db;Username=ops;Password=••••••;SSL Mode=Require",
            panel.MaskedConnectionString);

        panel.ConnectionString = "Server=db;Pwd=x";
        Assert.Equal("Server=db;Pwd=••••••", panel.MaskedConnectionString);

        // A bare file path has nothing to hide and stays untouched.
        panel.ConnectionString = "/data/app.db";
        Assert.Equal("/data/app.db", panel.MaskedConnectionString);
    }

    [Fact]
    public void Dispose_is_idempotent_across_tab_and_window_teardown()
    {
        var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            new FakeDatabasePanelClient());

        panel.Dispose();
        panel.Dispose();
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

        public ConnectionProfile? LastTunnel { get; private set; }

        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("sqlite", "SQLite", "Data Source=…"),
            new("postgres", "PostgreSQL", "Host=…"),
        ];

        public string? LastConnectionString { get; private set; }

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
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
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
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

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString) =>
            connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase)
                ? new(Options: connectionString, Password: "present")
                : new(Options: connectionString);

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details) =>
            details.Password is { } password
                ? $"{details.FilePath ?? details.Options};Password={password}"
                : details.FilePath ?? details.Options ?? string.Empty;

        private void ThrowIfConfigured()
        {
            if (FailWith is { } message)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
