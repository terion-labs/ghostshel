using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure.Tests;

public sealed class SqliteDatabaseTests
{
    [Fact]
    public async Task InitializationEnablesWalForeignKeysAndCurrentMigration()
    {
        await using var temporary = TemporaryDatabase.Create();

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(
            SqliteSchema.Migrations[^1].Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        // 3.49.1 is what SQLite3 Multiple Ciphers currently bundles — the
        // price of encryption at rest; raise alongside GhostShellDatabase's
        // own minimum as that bundle tracks upstream.
        var version = Version.Parse(await ScalarAsync(connection, "SELECT sqlite_version();"));
        Assert.True(version >= new Version(3, 49, 1));
    }

    [Fact]
    public async Task MigrationChecksumDriftStopsStartup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'changed' WHERE version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await temporary.ReopenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseFromANewerApplicationVersionStopsStartup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        var futureVersion = 0;
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            futureVersion = int.Parse(
                await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"),
                System.Globalization.CultureInfo.InvariantCulture) + 1;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc)
                VALUES ($version, 'future-schema', 'future-checksum', $appliedUtc);
                """;
            command.Parameters.AddWithValue("$version", futureVersion);
            command.Parameters.AddWithValue(
                "$appliedUtc",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        await temporary.ReopenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains(
            $"unsupported schema version {futureVersion}",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<int> HistoricalSchemaVersions
    {
        get
        {
            var versions = new TheoryData<int>();
            foreach (var migration in SqliteSchema.Migrations.SkipLast(1))
            {
                versions.Add(migration.Version);
            }

            return versions;
        }
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemaVersions))]
    public async Task HistoricalSchemasUpgradeWithoutLosingDurableOrRecoveryState(
        int sourceVersion)
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            sourceVersion);

        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                HistoricalDatabaseFixture.DefinitionName,
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT name
                    FROM definitions
                    WHERE kind = 'layout'
                        AND id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT COUNT(*)
                    FROM audit_events
                    WHERE event_id = '{HistoricalDatabaseFixture.AuditEventId}';
                    """));
            Assert.Equal(
                "2",
                await ScalarAsync(connection, "SELECT COUNT(*) FROM runtime_snapshots;"));
            Assert.Equal(
                sourceVersion >= 2 ? "1" : "0",
                await ScalarAsync(
                    connection,
                    $"""
                    SELECT COUNT(*)
                    FROM recent_sessions
                    WHERE session_id = '{HistoricalDatabaseFixture.RecentSessionId}';
                    """));
            // Upgrading turns session history off wherever it was never
            // deliberately chosen: retaining a record of every session opened is
            // opt-in, and an upgrade is not the user opting in.
            Assert.Equal(
                "0",
                await ScalarAsync(
                    connection,
                    """
                    SELECT maximum_entries
                    FROM recent_session_retention
                    WHERE singleton_id = 1;
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    connection,
                    """
                    SELECT completed_version
                    FROM onboarding_progress
                    WHERE singleton_id = 1;
                    """));
            await AssertDatabaseIntegrityAsync(connection);
        }

        var catalog = CreateDefinitionCatalog(temporary.Database);
        var catalogResult = await catalog.InitializeAsync(CancellationToken.None);
        Assert.True(catalogResult.IsSuccess, catalogResult.Error?.Message);
        var restoredLayout = Assert.Single(
            catalogResult.Value!.Layouts,
            item => item.Value.Id.Value == HistoricalDatabaseFixture.DefinitionId);
        Assert.Equal(HistoricalDatabaseFixture.DefinitionName, restoredLayout.Value.Name);

        var runStore = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);
        var currentRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True(currentRun.RecoveryRequired);
        Assert.Equal(
            HistoricalDatabaseFixture.InterruptedRunId,
            currentRun.PreviousState.RunId);

        var startupState = new ApplicationStartupState();
        startupState.Initialize(currentRun);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var coordinator = new RecoveryCoordinator(recoveryStore, startupState);
        var restored = Success(await coordinator.ResolveAsync(
            RecoveryChoice.Restore,
            CancellationToken.None));

        var snapshot = Assert.Single(restored);
        Assert.Equal(HistoricalDatabaseFixture.InterruptedRunId, snapshot.RunId);
        Assert.Equal(HistoricalDatabaseFixture.InterruptedSnapshotKey, snapshot.Key);
        Assert.Single(Success(await recoveryStore.LoadAsync(
            HistoricalDatabaseFixture.OtherRunId,
            CancellationToken.None)));
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemaVersions))]
    public async Task FailedHistoricalUpgradeRollsBackAndCanRetry(
        int sourceVersion)
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            sourceVersion);
        await HistoricalDatabaseFixture.AddNextMigrationCollisionAsync(
            temporary.DatabasePath,
            sourceVersion);

        // Most migrations are obstructed by pre-creating the object they create;
        // one that only rewrites a row is obstructed by refusing the write. What
        // matters either way is that it failed and left the schema where it was.
        var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            await temporary.Database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains(
            sourceVersion == 8 ? "next migration collision" : "already exists",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                sourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(
                    unchanged,
                    "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    unchanged,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            Assert.Equal(
                "2",
                await ScalarAsync(unchanged, "SELECT COUNT(*) FROM runtime_snapshots;"));
            await AssertDatabaseIntegrityAsync(unchanged);
        }

        await HistoricalDatabaseFixture.RemoveNextMigrationCollisionAsync(
            temporary.DatabasePath,
            sourceVersion);
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var migrated = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        Assert.Equal(
            SqliteSchema.Migrations[^1].Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        await AssertDatabaseIntegrityAsync(migrated);
    }

    [Fact]
    public async Task FailedMigrationRollsBackEarlierStatementsAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var migration = new SqliteMigration(
            SqliteSchema.Migrations[^1].Version + 1,
            "transaction-rollback-probe",
            """
            CREATE TABLE migration_probe (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
            );

            INSERT INTO migration_probe(singleton_id) VALUES (1);
            INSERT INTO migration_gate(value) VALUES ('opened');
            """);
        await using var database = CreateDatabase(temporary.DatabasePath, migration);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'migration_probe';
                    """));
            await using var gate = unchanged.CreateCommand();
            gate.CommandText = "CREATE TABLE migration_gate (value TEXT NOT NULL);";
            await gate.ExecuteNonQueryAsync();
        }

        await database.EnsureInitializedAsync(CancellationToken.None);
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            "1",
            await ScalarAsync(migrated, "SELECT COUNT(*) FROM migration_probe;"));
        Assert.Equal(
            "opened",
            await ScalarAsync(migrated, "SELECT value FROM migration_gate;"));
        await AssertDatabaseIntegrityAsync(migrated);
    }

    [Fact]
    public async Task DestructiveMigrationCreatesAValidatedRestorableBackup()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var migration = DestructiveProbeMigration();
        var options = new SqliteStorageOptions(temporary.DatabasePath);
        await using var database = new GhostShellDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        await database.EnsureInitializedAsync(CancellationToken.None);

        var backupPath = Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"ghostshell-before-v{migration.Version}-*.db"));
        var backupBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using (var backup = new SqliteConnection(backupBuilder.ConnectionString))
        {
            await backup.OpenAsync();
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(backup, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    backup,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    backup,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            await AssertDatabaseIntegrityAsync(backup);
        }

        await using (var migrated = await database.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(
                migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "1",
                await ScalarAsync(migrated, "SELECT COUNT(*) FROM destructive_probe;"));
        }

        await database.DisposeAsync();
        await using var restoredDatabase = new GhostShellDatabase(
            new SqliteStorageOptions(backupPath),
            TimeProvider.System);
        await restoredDatabase.EnsureInitializedAsync(CancellationToken.None);
        var restoredLayouts = new SqliteDefinitionRepository<LayoutDefinition>(
            restoredDatabase,
            TimeProvider.System);
        var restored = await restoredLayouts.GetAsync(
            new DefinitionKey(LayoutDefinition.Kind, HistoricalDatabaseFixture.DefinitionId),
            CancellationToken.None);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(HistoricalDatabaseFixture.DefinitionName, restored.Value!.Value.Name);
    }

    [Fact]
    public async Task BackupValidationFailurePublishesNothingAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        await using (var invalid = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            await using var command = invalid.CreateCommand();
            command.CommandText = $"""
                PRAGMA foreign_keys = OFF;
                INSERT INTO definition_references(
                    owner_kind, owner_id, target_kind, target_id, role)
                VALUES (
                    'layout', 'missing-owner', 'layout',
                    '{HistoricalDatabaseFixture.DefinitionId}', 'fixture');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new SqliteStorageOptions(temporary.DatabasePath);
        var migration = DestructiveProbeMigration();
        await using var database = new GhostShellDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));
        Assert.Contains("foreign-key validation", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(options.BackupDirectory));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            await using var repair = unchanged.CreateCommand();
            repair.CommandText = """
                DELETE FROM definition_references
                WHERE owner_kind = 'layout' AND owner_id = 'missing-owner';
                """;
            Assert.Equal(1, await repair.ExecuteNonQueryAsync());
        }

        await database.EnsureInitializedAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"ghostshell-before-v{migration.Version}-*.db"));
        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(options.BackupDirectory),
            path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal));
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public async Task BackupCreationFailureLeavesSourceUnchangedAndCanRetry()
    {
        await using var temporary = TemporaryDatabase.Create();
        await HistoricalDatabaseFixture.CreateAsync(
            temporary.DatabasePath,
            SqliteSchema.Migrations[^1].Version);
        var options = new SqliteStorageOptions(temporary.DatabasePath);
        await File.WriteAllTextAsync(options.BackupDirectory, "not-a-directory");
        var migration = DestructiveProbeMigration();
        await using var database = new GhostShellDatabase(
            options,
            TimeProvider.System,
            [.. SqliteSchema.Migrations, migration]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await database.EnsureInitializedAsync(CancellationToken.None));

        await using (var unchanged = await HistoricalDatabaseFixture.OpenAsync(
            temporary.DatabasePath))
        {
            Assert.Equal(
                SqliteSchema.Migrations[^1].Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await ScalarAsync(unchanged, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(
                "0",
                await ScalarAsync(
                    unchanged,
                    """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'destructive_probe';
                    """));
            Assert.Equal(
                "1",
                await ScalarAsync(
                    unchanged,
                    $"""
                    SELECT COUNT(*)
                    FROM definitions
                    WHERE id = '{HistoricalDatabaseFixture.DefinitionId}';
                    """));
            await AssertDatabaseIntegrityAsync(unchanged);
        }

        File.Delete(options.BackupDirectory);
        await database.EnsureInitializedAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(
            options.BackupDirectory,
            $"ghostshell-before-v{migration.Version}-*.db"));
        await using var migrated = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            migration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            "1",
            await ScalarAsync(migrated, "SELECT COUNT(*) FROM destructive_probe;"));
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static GhostShellDatabase CreateDatabase(
        string databasePath,
        SqliteMigration additionalMigration) =>
        new(
            new SqliteStorageOptions(databasePath),
            TimeProvider.System,
            [.. SqliteSchema.Migrations, additionalMigration]);

    private static DefinitionCatalog CreateDefinitionCatalog(GhostShellDatabase database)
    {
        var timeProvider = TimeProvider.System;
        return new DefinitionCatalog(
            new SqliteDefinitionRepository<ConnectionProfile>(database, timeProvider),
            new SqliteDefinitionRepository<LayoutDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<ScreenDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<WorkspaceDefinition>(database, timeProvider),
            new SqliteDefinitionRepository<ThemePreference>(database, timeProvider),
            new SqliteDefinitionRepository<TerminalProfile>(database, timeProvider),
            new SqliteDefinitionRepository<KeymapProfile>(database, timeProvider),
            new SqliteDefinitionRepository<FileProviderProfile>(database, timeProvider),
            new SqliteDefinitionRepository<AiProviderProfile>(database, timeProvider),
            new SqliteDefinitionRepository<McpServerProfile>(database, timeProvider),
            new SqliteDefinitionRepository<QuickTerminalSettings>(database, timeProvider));
    }

    private static SqliteMigration DestructiveProbeMigration() => new(
        SqliteSchema.Migrations[^1].Version + 1,
        "destructive-backup-probe",
        """
        CREATE TABLE destructive_probe (
            singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
        );

        INSERT INTO destructive_probe(singleton_id) VALUES (1);
        """,
        IsDestructive: true);

    private static async Task AssertDatabaseIntegrityAsync(SqliteConnection connection)
    {
        Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static T Success<T>(ApplicationRunResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
