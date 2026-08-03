using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class UnifiedConnectionEditorViewModelTests
{
    [Fact]
    public void Type_options_span_every_available_family()
    {
        var editor = CreateEditor();

        var families = editor.TypeOptions
            .Select(option => option.Family)
            .Distinct()
            .ToArray();

        Assert.Equal(
            [
                SavedConnectionFamily.Terminal,
                SavedConnectionFamily.Files,
                SavedConnectionFamily.Database,
            ],
            families);
        Assert.Contains(editor.TypeOptions, option => option.DisplayName == "Terminal · SSH");
        Assert.Contains(editor.TypeOptions, option => option.DisplayName == "Files · SFTP");
        Assert.Contains(editor.TypeOptions, option => option.DisplayName == "Database · PostgreSQL");
    }

    [Fact]
    public void Locked_family_restricts_the_type_selector_to_that_family()
    {
        var editor = CreateEditor(
            lockedFamily: SavedConnectionFamily.Files,
            initialFamily: SavedConnectionFamily.Files);

        Assert.All(
            editor.TypeOptions,
            option => Assert.Equal(SavedConnectionFamily.Files, option.Family));
        Assert.True(editor.IsFiles);
    }

    [Fact]
    public void Selecting_a_type_switches_family_and_pushes_the_kind_down()
    {
        var editor = CreateEditor();

        editor.SelectedType = editor.TypeOptions
            .Single(option => option.DisplayName == "Files · WebDAV");

        Assert.True(editor.IsFiles);
        Assert.Equal(FileProviderKind.WebDav, editor.Files!.Kind);

        editor.SelectedType = editor.TypeOptions
            .Single(option => option.DisplayName == "Database · SQLite");

        Assert.True(editor.IsDatabase);
        Assert.Equal("sqlite", editor.Database!.SelectedDriver.Id);
        Assert.False(editor.CanTest);
    }

    [Fact]
    public void Name_survives_switching_families()
    {
        var editor = CreateEditor();
        editor.Name = "Production";

        editor.SelectedType = editor.TypeOptions
            .Single(option => option.DisplayName == "Database · PostgreSQL");

        Assert.Equal("Production", editor.Name);
        Assert.Equal("Production", editor.Database!.Name);
    }

    [Fact]
    public void Save_result_matches_the_selected_family()
    {
        var editor = CreateEditor();
        editor.Name = "Local box";

        var terminal = Assert.IsType<UnifiedConnectionEditorResult.Terminal>(
            editor.CreateSaveResult(saveConnection: false));
        Assert.False(terminal.SaveConnection);
        Assert.Equal("Local box", terminal.Request.Profile.Name);

        editor.SelectedType = editor.TypeOptions
            .Single(option => option.DisplayName == "Database · PostgreSQL");
        editor.Database!.Host = "db.example";
        editor.Database.Port = "5433";
        editor.Database.DatabaseName = "app";

        var database = Assert.IsType<UnifiedConnectionEditorResult.Database>(
            editor.CreateSaveResult());
        Assert.Equal("postgres", database.Request.DriverId);
        Assert.Equal("db.example", database.Request.Details.Host);
        Assert.Equal(5433, database.Request.Details.Port);
    }

    [Fact]
    public void Database_editor_round_trips_an_existing_profile()
    {
        var tunnel = SshConnection("bastion");
        var existing = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Analytics",
            "postgres",
            "Host=warehouse;Port=5432;Database=events;Username=reader",
            passwordSecret: new SecretRef("stored-password"),
            tunnelConnectionId: tunnel.Id);

        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [tunnel],
            existing);

        Assert.True(editor.IsEditing);
        Assert.True(editor.HasStoredPassword);
        Assert.Equal("Analytics", editor.Name);
        Assert.Equal("postgres", editor.SelectedDriver.Id);
        Assert.Equal("warehouse", editor.Host);
        Assert.Equal("5432", editor.Port);
        Assert.Equal("events", editor.DatabaseName);
        Assert.Equal("reader", editor.Username);
        Assert.Equal(tunnel.Id, editor.SelectedTunnel.Id);

        var request = editor.CreateSaveRequest();
        Assert.Equal(existing.Id, request.ExistingId);
        Assert.Null(request.Details.Password);
        Assert.Equal(tunnel.Id, request.TunnelConnectionId);
    }

    [Fact]
    public void Database_editor_validates_name_port_and_file_path()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            []);

        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.Name = "Broken";
        editor.Port = "not-a-port";
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.SelectedDriver = editor.Drivers.Single(driver => driver.Id == "sqlite");
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.FilePath = "/data/app.db";
        var request = editor.CreateSaveRequest();
        Assert.Equal("/data/app.db", request.Details.FilePath);
    }

    [Fact]
    public void Database_tunnel_options_offer_only_ssh_connections()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [SshConnection("bastion"), LocalConnection("local shell")]);

        Assert.Equal(
            ["No tunnel", "bastion"],
            editor.TunnelOptions.Select(option => option.DisplayName).ToArray());
    }

    private static UnifiedConnectionEditorViewModel CreateEditor(
        SavedConnectionFamily? lockedFamily = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = new ConnectionEditorViewModel(new StubConnectionRuntime());
        var files = lockedFamily is null or SavedConnectionFamily.Files
            ? new FileProviderProfileEditorViewModel(
                new StubProviderRuntime(),
                [SshConnection("bastion")],
                [])
            : null;
        var database = lockedFamily is null or SavedConnectionFamily.Database
            ? new DatabaseConnectionEditorViewModel(
                new StructuralDatabaseClient(),
                [SshConnection("bastion")])
            : null;
        return new UnifiedConnectionEditorViewModel(
            terminal,
            files,
            database,
            lockedFamily,
            initialFamily);
    }

    private static ConnectionProfile SshConnection(string name) => new(
        ConnectionId.New(),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Ssh("bastion.example", username: "ops"),
        new ConnectionAuthentication.SshAgent(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private static ConnectionProfile LocalConnection(string name) => new(
        ConnectionId.New(),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private sealed class StubConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    profile.Id,
                    profile.ConnectionKind,
                    ConnectionTestVerification.RuntimeAvailable,
                    false)));
    }

    private sealed class StubProviderRuntime : IFileProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(true, "ok", profile.Name));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A structural fake that parses and rebuilds "Key=Value;…" strings, so the
    /// round-trip assertions exercise real field mapping rather than passthrough.
    /// </summary>
    private sealed class StructuralDatabaseClient : IDatabasePanelClient
    {
        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("postgres", "PostgreSQL", "Host=…"),
            new("sqlite", "SQLite", "/path/to/database.db", IsFileBased: true),
        ];

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>([]);

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
            $"SELECT * FROM {tableName} LIMIT {limit};";

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString)
        {
            var values = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            return new DatabaseConnectionDetails(
                values.GetValueOrDefault("Host"),
                values.TryGetValue("Port", out var port) ? int.Parse(port) : null,
                values.GetValueOrDefault("Database"),
                values.GetValueOrDefault("Username"),
                values.GetValueOrDefault("Password"),
                values.GetValueOrDefault("Data Source"));
        }

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
        {
            var pairs = new List<string>();
            if (details.FilePath is { } filePath)
            {
                pairs.Add($"Data Source={filePath}");
            }

            if (details.Host is { } host)
            {
                pairs.Add($"Host={host}");
            }

            if (details.Port is { } port)
            {
                pairs.Add($"Port={port}");
            }

            if (details.Database is { } database)
            {
                pairs.Add($"Database={database}");
            }

            if (details.Username is { } username)
            {
                pairs.Add($"Username={username}");
            }

            if (details.Password is { } password)
            {
                pairs.Add($"Password={password}");
            }

            return string.Join(';', pairs);
        }
    }
}
