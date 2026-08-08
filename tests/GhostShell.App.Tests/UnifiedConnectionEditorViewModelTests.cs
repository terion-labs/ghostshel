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
        Assert.True(editor.CanTest);
        Assert.Equal("Test", editor.TestLabel);
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
            ["No tunnel", "bastion", "Custom — this connection only"],
            editor.TunnelOptions.Select(option => option.DisplayName).ToArray());
    }

    [Fact]
    public void A_pasted_connection_string_is_the_other_view_of_the_same_fields()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            []);
        editor.Name = "Pasted";
        editor.UseConnectionString = true;
        editor.ConnectionStringInput = "Host=db.example;Port=5433;Database=app;Username=reader;Password=secret";

        var request = editor.CreateSaveRequest();
        Assert.Equal("db.example", request.Details.Host);
        Assert.Equal(5433, request.Details.Port);
        Assert.Equal("secret", request.Details.Password);

        // Switching back to the fields reads the string into them.
        editor.UseConnectionString = false;
        Assert.Equal("db.example", editor.Host);
        Assert.Equal("5433", editor.Port);
        Assert.Equal("app", editor.DatabaseName);
        Assert.Equal("reader", editor.Username);
        Assert.Equal("secret", editor.Password);
    }

    [Fact]
    public void Switching_to_the_string_view_prefills_from_fields_without_the_password()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            []);
        editor.Host = "db.example";
        editor.Port = "5432";
        editor.DatabaseName = "app";
        editor.Password = "secret";

        editor.UseConnectionString = true;

        Assert.Contains("Host=db.example", editor.ConnectionStringInput, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", editor.ConnectionStringInput, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_tunnel_travels_in_the_save_request_instead_of_a_reference()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [SshConnection("bastion")]);
        editor.Name = "Tunnelled";
        editor.Host = "db.internal";
        editor.SelectedTunnel = editor.TunnelOptions.Single(option => option.IsInline);
        editor.TunnelHost = "bastion.example";
        editor.TunnelUsername = "ops";

        var request = editor.CreateSaveRequest();

        Assert.Null(request.TunnelConnectionId);
        Assert.NotNull(request.InlineTunnel);
        var inline = request.InlineTunnel!;
        Assert.Equal("bastion.example", inline.Host);
        Assert.Equal(22, inline.Port);
        Assert.Equal("ops", inline.Username);
        Assert.True(inline.UseAgent);
    }

    [Fact]
    public void An_inline_password_tunnel_without_any_password_is_refused()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            []);
        editor.Name = "Tunnelled";
        editor.Host = "db.internal";
        editor.SelectedTunnel = editor.TunnelOptions.Single(option => option.IsInline);
        editor.TunnelHost = "bastion.example";
        editor.TunnelAuthentication = DatabaseConnectionEditorViewModel.PasswordAuthentication;

        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.TunnelPassword = "hunter2";
        var inline = editor.CreateSaveRequest().InlineTunnel;
        Assert.NotNull(inline);
        Assert.False(inline!.UseAgent);
        Assert.Equal("hunter2", inline.Password);
    }

    [Fact]
    public void An_existing_inline_tunnel_round_trips_through_the_editor()
    {
        var profileId = DatabaseConnectionProfileId.New();
        var inline = DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
            DatabaseConnectionProfile.InlineTunnelId(profileId),
            "Analytics tunnel",
            new DatabaseInlineTunnelRequest("bastion.example", 2222, "ops", false, "kept"),
            new ConnectionAuthentication.Password(new SecretRef("tunnel-password")));
        var existing = new DatabaseConnectionProfile(
            profileId,
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Analytics",
            "postgres",
            "Host=warehouse;Database=events",
            inlineTunnel: inline);

        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [],
            existing);

        Assert.True(editor.SelectedTunnel.IsInline);
        Assert.Equal("bastion.example", editor.TunnelHost);
        Assert.Equal("2222", editor.TunnelPort);
        Assert.Equal("ops", editor.TunnelUsername);
        Assert.True(editor.HasStoredTunnelPassword);
        Assert.True(editor.TunnelUsesPassword);

        // Saving without retyping keeps the stored tunnel password.
        var request = editor.CreateSaveRequest();
        Assert.NotNull(request.InlineTunnel);
        var kept = request.InlineTunnel!;
        Assert.False(kept.UseAgent);
        Assert.Null(kept.Password);
    }

    [Fact]
    public async Task The_database_test_reports_the_servers_session_facts()
    {
        var client = new StructuralDatabaseClient
        {
            SessionInfo = new DatabaseSessionInfo("16.4", "TLSv1.3"),
        };
        var editor = new DatabaseConnectionEditorViewModel(client, []);
        editor.Name = "Probe";
        editor.Host = "db.example";
        editor.Password = "secret";

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Connected", editor.TestStatus);
        Assert.Contains("16.4", editor.TestDetail, StringComparison.Ordinal);
        Assert.Contains("TLSv1.3", editor.TestDetail, StringComparison.Ordinal);
        // The probe connects with the typed password even though the saved
        // string never carries it.
        Assert.Contains("Password=secret", client.LastProbeConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_database_test_reports_the_reason_and_keeps_the_dialog_open()
    {
        var client = new StructuralDatabaseClient
        {
            ProbeError = new InvalidOperationException("connection refused"),
        };
        var editor = new DatabaseConnectionEditorViewModel(client, []);
        editor.Name = "Probe";
        editor.Host = "db.example";

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Test failed", editor.TestStatus);
        Assert.Equal("connection refused", editor.TestDetail);
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
            new("postgres", "PostgreSQL", "Host=…", DefaultPort: 5432, CanListDatabases: true),
            new("sqlite", "SQLite", "/path/to/database.db", IsFileBased: true),
        ];

        public DatabaseSessionInfo SessionInfo { get; init; } = new();

        public Exception? ProbeError { get; init; }

        public string? LastProbeConnectionString { get; private set; }

        public Task<DatabaseSessionInfo> DescribeSessionAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            LastProbeConnectionString = connectionString;
            return ProbeError is { } error
                ? Task.FromException<DatabaseSessionInfo>(error)
                : Task.FromResult(SessionInfo);
        }

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
