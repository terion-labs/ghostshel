using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure.Tests;

internal static class HistoricalDatabaseFixture
{
    public const string DefinitionId = "layout-existing";
    public const string DefinitionName = "Existing layout";
    public const string InterruptedRunId = "run-interrupted";
    public const string InterruptedSnapshotKey = "desktop.main-window";
    public const string OtherRunId = "run-other";
    public const string RecentSessionId = "session-existing";
    public const string AuditEventId = "event-existing";

    private const string DefinitionPayload =
        """{"id":{"value":"layout-existing"},"schemaVersion":1,"name":"Existing layout","grid":{"columns":1,"rows":1},"slots":[{"id":{"value":"main"},"bounds":{"column":0,"row":0,"columnSpan":1,"rowSpan":1},"minimumSize":{"width":160,"height":100}}]}""";
    private const string RecoveryPayload = "{}";

    private static readonly IReadOnlyDictionary<int, FrozenMigrationReceipt> FrozenMigrations =
        new Dictionary<int, FrozenMigrationReceipt>
        {
            [1] = new(
                "durable-definitions-and-recovery",
                "A50577BDFD6FF8838E9FC23C78FCBC82A7FB0DCA16F9921351336170CA87367C",
                IsDestructive: false),
            [2] = new(
                "bounded-recent-session-history",
                "F44E5718FFA889BD13A89867FF545AFD3BC3B6873BED106772C9A6DC7E460595",
                IsDestructive: false),
            [3] = new(
                "revisioned-recent-session-retention",
                "02BD2F718F1860C3BDA9BC0F864D6009670BAC1CC4CA3D4BDAAD747AF8E60A73",
                IsDestructive: false),
            [4] = new(
                "versioned-first-run-progress",
                "F776BF5851BDF9FB58F4A6AB85F203B582EA8865C87EF4D40B95475194F32325",
                IsDestructive: false),
            [5] = new(
                "durable-agent-action-audit-state",
                "4894FAD84D4564429553057C3AB1C65D0F3E1C3795F40E353A94D3D93BC3A1F1",
                IsDestructive: false),
            [6] = new(
                "indexed-agent-run-audit-reading",
                "D8C1BEDF1C2E218569363632927A1B2CF78E37DA4B88494B853AC28267B9F320",
                IsDestructive: false),
            [7] = new(
                "session-restore-preference",
                "653DC5C26135B69989F1F543AD93AE14971163B11F82B4E1E4BB4708E75F53B7",
                IsDestructive: false),
        };

    public static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 23, 8, 30, 0, TimeSpan.Zero);

    public static async Task CreateAsync(string databasePath, int schemaVersion)
    {
        var migrations = SqliteSchema.Migrations
            .Where(migration => migration.Version <= schemaVersion)
            .ToArray();
        if (migrations.Length == 0
            || migrations[^1].Version != schemaVersion
            || schemaVersion > SqliteSchema.Migrations[^1].Version)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The fixture schema version must identify a supported migration.");
        }

        await using var connection = await OpenAsync(databasePath);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                checksum TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """);

        foreach (var migration in migrations)
        {
            var receipt = RequireFrozenMigration(migration);
            await ApplyMigrationAsync(connection, migration, receipt);
        }

        await InsertSentinelsAsync(connection, schemaVersion);
    }

    public static async Task AddNextMigrationCollisionAsync(
        string databasePath,
        int currentVersion)
    {
        var schemaObjectName = NextMigrationSchemaObject(currentVersion);
        await using var connection = await OpenAsync(databasePath);
        await ExecuteAsync(
            connection,
            currentVersion == 5
                ? $"CREATE INDEX {schemaObjectName} ON audit_events(sequence);"
                : $"CREATE TABLE {schemaObjectName} "
                  + "(incompatible_column TEXT NOT NULL);");
    }

    public static async Task RemoveNextMigrationCollisionAsync(
        string databasePath,
        int currentVersion)
    {
        var schemaObjectName = NextMigrationSchemaObject(currentVersion);
        await using var connection = await OpenAsync(databasePath);
        await ExecuteAsync(
            connection,
            currentVersion == 5
                ? $"DROP INDEX {schemaObjectName};"
                : $"DROP TABLE {schemaObjectName};");
    }

    public static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<string> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture)!;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SqliteMigration migration,
        FrozenMigrationReceipt receipt)
    {
        await using var transaction = connection.BeginTransaction();
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = migration.Sql;
            await schema.ExecuteNonQueryAsync();
        }

        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc)
                VALUES ($version, $name, $checksum, $appliedUtc);
                """;
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", receipt.Name);
            record.Parameters.AddWithValue("$checksum", receipt.Checksum);
            record.Parameters.AddWithValue(
                "$appliedUtc",
                ReferenceTime.ToString("O", CultureInfo.InvariantCulture));
            await record.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task InsertSentinelsAsync(
        SqliteConnection connection,
        int schemaVersion)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO definitions(
                kind, id, schema_version, revision, name, payload_json,
                created_utc, updated_utc)
            VALUES (
                'layout', $definitionId, 1, 1, $definitionName, $definitionPayload,
                $timestamp, $timestamp);

            UPDATE app_lifecycle
            SET clean_shutdown = 0,
                current_run_id = $interruptedRunId,
                started_utc = $timestamp,
                last_clean_utc = NULL
            WHERE singleton_id = 1;

            INSERT INTO runtime_snapshots(
                run_id, snapshot_key, schema_version, payload_json, updated_utc)
            VALUES
                ($interruptedRunId, $interruptedSnapshotKey, 1,
                    $recoveryPayload, $timestamp),
                ($otherRunId, 'desktop.other-window', 1,
                    $recoveryPayload, $timestamp);

            INSERT INTO audit_events(
                event_id, correlation_id, actor_kind, actor_id, action,
                target_kind, target_id, outcome, details_json, occurred_utc)
            VALUES (
                $auditEventId, 'correlation-existing', 'System', 'system-existing',
                'fixture.created', NULL, NULL, 'Succeeded',
                '{"schemaVersion":1,"kind":"none"}', $timestamp);
            """;
        command.Parameters.AddWithValue("$definitionId", DefinitionId);
        command.Parameters.AddWithValue("$definitionName", DefinitionName);
        command.Parameters.AddWithValue("$definitionPayload", DefinitionPayload);
        command.Parameters.AddWithValue("$interruptedRunId", InterruptedRunId);
        command.Parameters.AddWithValue("$interruptedSnapshotKey", InterruptedSnapshotKey);
        command.Parameters.AddWithValue("$otherRunId", OtherRunId);
        command.Parameters.AddWithValue("$recoveryPayload", RecoveryPayload);
        command.Parameters.AddWithValue("$auditEventId", AuditEventId);
        command.Parameters.AddWithValue(
            "$timestamp",
            ReferenceTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();

        if (schemaVersion >= 2)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO recent_sessions(
                    session_id, definition_kind, definition_id, panel_kind, title,
                    started_utc, ended_utc, outcome)
                VALUES (
                    $sessionId, 'layout', $definitionId, 'Terminal',
                    'Existing session', $timestamp, NULL, 'Active');
                """;
            command.Parameters.AddWithValue("$sessionId", RecentSessionId);
            command.Parameters.AddWithValue("$definitionId", DefinitionId);
            command.Parameters.AddWithValue(
                "$timestamp",
                ReferenceTime.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        if (schemaVersion >= 4)
        {
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE onboarding_progress
                SET completed_version = 1
                WHERE singleton_id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static FrozenMigrationReceipt RequireFrozenMigration(SqliteMigration migration)
    {
        var checksum = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql)));
        if (!FrozenMigrations.TryGetValue(migration.Version, out var receipt)
            || !string.Equals(checksum, receipt.Checksum, StringComparison.Ordinal)
            || !string.Equals(migration.Name, receipt.Name, StringComparison.Ordinal)
            || migration.IsDestructive != receipt.IsDestructive)
        {
            throw new InvalidOperationException(
                $"Migration {migration.Version} no longer matches its released fixture receipt. "
                + "Add a forward migration instead of editing a shipped schema.");
        }

        return receipt;
    }

    private static string NextMigrationSchemaObject(int currentVersion) =>
        currentVersion switch
        {
            1 => "recent_sessions",
            2 => "recent_session_retention",
            3 => "onboarding_progress",
            4 => "agent_action_audit_state",
            5 => "audit_events_agent_run_idx",
            6 => "session_restore_preference",
            _ => throw new ArgumentOutOfRangeException(
                nameof(currentVersion),
                currentVersion,
                "Only historical schemas with a later production migration are supported."),
        };

    private sealed record FrozenMigrationReceipt(
        string Name,
        string Checksum,
        bool IsDestructive);
}
