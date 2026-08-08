using System.Globalization;
using System.Reflection;
using Avalonia.Media;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.DesignQa;

/// <summary>
/// In-memory collaborators for the presentation-only design QA harness. They
/// never touch SQLite, the OS vault, terminal sessions, or the user profile.
/// </summary>
internal sealed class QaDefinitionCatalog : IDefinitionCatalog
{
    public QaDefinitionCatalog(DefinitionCatalogSnapshot snapshot) => Snapshot = snapshot;

    public DefinitionCatalogSnapshot Snapshot { get; private set; }

    public event EventHandler? Changed;

    public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot));

    public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot));

    private static ValueTask<DefinitionStoreResult<StoredDefinition<T>>> Store<T>(T definition)
        where T : IDurableDefinition =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<T>>.Success(
                new StoredDefinition<T>(
                    definition,
                    1,
                    QaData.Now,
                    QaData.Now)));

    public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    /// <summary>
    /// A saved workspace lands in the snapshot, for the same reason the theme
    /// does: the workspaces rail is drawn from the catalog, so a stub that
    /// reported success and changed nothing would capture a rail that
    /// contradicts the save it just made.
    /// </summary>
    public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition, long? expectedRevision, CancellationToken cancellationToken)
    {
        var stored = new StoredDefinition<WorkspaceDefinition>(
            definition,
            (expectedRevision ?? 0) + 1,
            QaData.Now,
            QaData.Now);
        Snapshot = Snapshot with
        {
            Workspaces =
            [
                .. Snapshot.Workspaces.Where(item => item.Value.Id != definition.Id),
                stored,
            ],
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(stored));
    }

    /// <summary>
    /// The saved appearance, kept so the harness can re-publish it exactly as
    /// the product does — "settings apply immediately" is itself a reviewable
    /// behavior, and a stub that swallowed the save made it uncapturable.
    /// </summary>
    public ThemePreference? SavedTheme { get; private set; }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition, long? expectedRevision, CancellationToken cancellationToken)
    {
        SavedTheme = definition;
        Changed?.Invoke(this, EventArgs.Empty);
        return Store(definition);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key, long expectedRevision, CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class ImmediateUiDispatcher : IUiThreadDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}

internal sealed class MemoryOnlySecretVault : ISecretVault
{
    public SecretVaultAvailability Availability { get; } = new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.MemoryOnly,
        SecretVaultCapabilities.ListMetadata,
        "qa",
        "qa_vault",
        "Isolated design QA vault");

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Dispose()
    {
    }
}

internal sealed class EmptyFileClients : IFilePanelClient, IFileTransferQueueClient
{
    private readonly List<FilePanelTransferSnapshot> _transfers = [];

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles => [];

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers => _transfers;

    public event EventHandler? TransfersChanged;

    public void PublishSampleTransfer()
    {
        var sourceRoot = new FilePanelLocation(
            "qa.files.source",
            "source",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var destinationRoot = new FilePanelLocation(
            "qa.files.destination",
            "destination",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var source = sourceRoot
            .Child(new FilePanelPathSegment("Archive.zip"));
        var destination = destinationRoot
            .Child(new FilePanelPathSegment("Archive.zip"));
        var request = new FilePanelTransferRequest(
            source,
            destination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.KeepBoth);
        _transfers.Add(new FilePanelTransferSnapshot(
            new FilePanelTransferId(
                Guid.Parse("e3c4b9cc-96f1-4eb4-8220-cb760c970ae3")),
            request,
            destination,
            FilePanelTransferState.Running,
            "Writing destination",
            700_000_000,
            5_000_000_000,
            null,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 31, 12, 0, 1, TimeSpan.Zero),
            null));
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        if (_transfers.Count == 0)
        {
            return;
        }

        _transfers.Clear();
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(new FilePanelPage([], null)));

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class MemoryOnlyAuditStore : IAuditStore
{
    public ValueTask<AuditStoreResult<Unit>> AppendAsync(
        AuditEventRecord auditEvent, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>> ListByCorrelationAsync(
        string correlationId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
}

internal sealed class UnusedConnectionRuntime : IConnectionRuntime
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
        throw new NotSupportedException();
}

public class UnusedProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException(targetMethod?.Name ?? "unknown method");
}

/// <summary>
/// A fixed set of recent sessions so the Home page's Recent Sessions section is
/// reviewable. Sessions are read-only here: recording and clearing succeed
/// without changing anything, because a capture run must not depend on the order
/// its routes happen to run in.
/// </summary>
internal sealed class QaRecentSessionStore : IRecentSessionStore
{
    private static readonly RecentSessionRecord[] Records =
    [
        Record("qa-session-api", "production-api", "production-api", TimeSpan.FromMinutes(2)),
        Record("qa-session-cache", "redis-cache", "redis-cache", TimeSpan.FromHours(1)),
        Record("qa-session-local", "local-dev", "local-dev", TimeSpan.FromHours(3)),
        Record("qa-session-db", "postgres-primary", "postgres-primary", TimeSpan.FromHours(15)),
    ];

    private static RecentSessionRecord Record(
        string sessionId,
        string connectionId,
        string title,
        TimeSpan age)
    {
        var endedAt = QaData.Now - age;
        return new RecentSessionRecord(
            new SessionId(sessionId),
            new DefinitionKey(ConnectionProfile.Kind, connectionId),
            PanelKind.Terminal,
            title,
            endedAt - TimeSpan.FromMinutes(12),
            endedAt,
            RecentSessionOutcome.GracefullyClosed);
    }

    public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        RecentSessionRecord recentSession,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        RecentSessionCompletion completion,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>> ListRecentAsync(
        RecentSessionQuery query,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(
            Records.Take(query.Limit).ToArray()));

    public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

    public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
        DateTimeOffset through,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

    public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
}

/// <summary>
/// Pins "now" to the fixture's timestamp so relative times in captures read the
/// same on every run instead of drifting with the wall clock.
/// </summary>
internal sealed class QaTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => QaData.Now;
}

/// <summary>
/// A file-provider runtime whose test always succeeds, so the unified editor's
/// files family renders without any provider adapter behind the harness.
/// </summary>
internal sealed class QaFileProviderRuntime : IFileProviderProfileRuntime
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
/// A synchronous database client so the viewer's table list and result grid are
/// reviewable without any database engine behind the harness.
/// </summary>
internal sealed class QaDatabasePanelClient : IDatabasePanelClient
{
    private static readonly IReadOnlyList<DatabaseColumnSchema> DeploymentSchema =
    [
        new(
            "id",
            1,
            "BIGINT",
            DatabaseValueKind.SignedInteger,
            typeof(long).FullName,
            IsNullable: false,
            IsPrimaryKey: true,
            PrimaryKeyOrdinal: 1,
            IsIdentity: true,
            IsReadOnly: true),
        new(
            "service",
            2,
            "VARCHAR(128)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: false,
            Length: 128),
        new(
            "region",
            3,
            "VARCHAR(32)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: false,
            Length: 32),
        new(
            "status",
            4,
            "VARCHAR(32)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: true,
            DefaultExpression: "'pending'",
            Length: 32),
        new(
            "deployed_at",
            5,
            "TIMESTAMP WITH TIME ZONE",
            DatabaseValueKind.TimestampWithZone,
            typeof(DateTimeOffset).FullName,
            IsNullable: false,
            DefaultExpression: "CURRENT_TIMESTAMP"),
    ];

    private static readonly IReadOnlyList<DatabaseIndexSchema> DeploymentIndexes =
    [
        new(
            "deployments_pkey",
            "btree",
            IsUnique: true,
            IsPrimary: true,
            IsValid: true,
            [new DatabaseIndexColumn("id", 1)]),
        new(
            "ix_deployments_service_region",
            "btree",
            IsUnique: false,
            IsPrimary: false,
            IsValid: true,
            [
                new DatabaseIndexColumn("service", 1),
                new DatabaseIndexColumn("region", 2),
                new DatabaseIndexColumn("status", 3, IsIncluded: true),
            ]),
        new(
            "ix_deployments_failures",
            "btree",
            IsUnique: false,
            IsPrimary: false,
            IsValid: true,
            [new DatabaseIndexColumn("deployed_at", 1, IsDescending: true)],
            Predicate: "status = 'rolled-back'"),
    ];

    private static readonly QaDeployment[] Deployments =
    [
        new(184, "billing-api", "eu-central-1", "healthy", At(21, 14, 9)),
        new(183, "billing-api", "us-east-1", "healthy", At(21, 12, 44)),
        new(182, "checkout-web", "eu-central-1", "rolled-back", At(19, 3, 18)),
        new(181, "ledger-worker", "eu-central-1", "healthy", At(17, 40, 51)),
        new(180, "ledger-worker", "us-east-1", null, At(17, 39, 12)),
        new(179, "checkout-web", "ap-south-1", "healthy", At(15, 22, 30)),
    ];

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
    [
        new("postgres", "PostgreSQL", "Host=localhost;Database=app;Username=postgres"),
        new("mysql", "MySQL", "Server=localhost;Database=app;User ID=root"),
        new("sqlite", "SQLite", "/path/to/database.db", IsFileBased: true),
    ];

    public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
        [
            new("deployments", DatabaseTableKind.Table),
            new("environments", DatabaseTableKind.Table),
            new("recent_failures", DatabaseTableKind.View),
            new("releases", DatabaseTableKind.Table),
            new("service_owners", DatabaseTableKind.View),
        ]);

    public Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateQueryPage(includeProvenance: false));

    public Task<DatabaseQueryPage> QueryWithProvenanceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateQueryPage(includeProvenance: true));

    public async Task<DatabaseTablePage> ReadQueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        _ = sourceSql;
        var page = await ReadTableAsync(
            driverId,
            connectionString,
            tunnel,
            new DatabaseTableDescriptor("deployments", DatabaseTableKind.Table),
            query,
            cancellationToken);
        return sourceColumns.Count == page.Result.Columns.Count
            ? page with { Result = page.Result with { Columns = sourceColumns } }
            : page;
    }

    public Task<long> CountQueryRowsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken)
    {
        _ = driverId;
        _ = connectionString;
        _ = tunnel;
        _ = sourceSql;
        _ = sourceColumns;
        cancellationToken.ThrowIfCancellationRequested();
        var count = Deployments.LongCount(row => filters.All(filter => Matches(row, filter)));
        return Task.FromResult(count);
    }

    private static DatabaseQueryPage CreateQueryPage(bool includeProvenance)
    {
        var baseObject = includeProvenance
            ? new DatabaseObjectId(null, null, "deployments")
            : null;
        return new DatabaseQueryPage(
            [
                new(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false,
                    IsKey: includeProvenance,
                    IsIdentity: includeProvenance,
                    IsReadOnly: includeProvenance,
                    BaseColumnName: includeProvenance ? "id" : null,
                    BaseObject: baseObject),
                new(
                    "service",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "service" : null,
                    BaseObject: baseObject),
                new(
                    "region",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "region" : null,
                    BaseObject: baseObject),
                new(
                    "status",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: true,
                    BaseColumnName: includeProvenance ? "status" : null,
                    BaseObject: baseObject),
                new(
                    "deployed_at",
                    "TEXT",
                    DatabaseValueKind.TimestampWithZone,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "deployed_at" : null,
                    BaseObject: baseObject),
            ],
            [
                new string?[] { "184", "billing-api", "eu-central-1", "healthy", "2026-08-02T21:14:09Z" },
                new string?[] { "183", "billing-api", "us-east-1", "healthy", "2026-08-02T21:12:44Z" },
                new string?[] { "182", "checkout-web", "eu-central-1", "rolled-back", "2026-08-02T19:03:18Z" },
                new string?[] { "181", "ledger-worker", "eu-central-1", "healthy", "2026-08-02T17:40:51Z" },
                new string?[] { "180", "ledger-worker", "us-east-1", null, "2026-08-02T17:39:12Z" },
                new string?[] { "179", "checkout-web", "ap-south-1", "healthy", "2026-08-02T15:22:30Z" },
            ],
            Truncated: false,
            RowsAffected: 0,
            TimeSpan.FromMilliseconds(12));
    }

    public Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canEdit = databaseObject.Kind == DatabaseTableKind.Table;
        return Task.FromResult(new DatabaseObjectDetails(
            databaseObject,
            DeploymentSchema,
            canEdit ? DeploymentIndexes : [],
            canEdit,
            canEdit ? null : "Views are read-only."));
    }

    public Task<DatabaseTablePage> ReadTableAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        IEnumerable<QaDeployment> rows = table.Name == "recent_failures"
            ? Deployments.Where(row => row.Status == "rolled-back")
            : Deployments;
        var filteredRows = rows
            .Where(row => query.Filters.All(filter => Matches(row, filter)))
            .ToArray();
        var ordered = Sort(filteredRows, query.Sorts);
        var window = ordered
            .Skip(query.Offset)
            .Take(query.Limit + 1)
            .ToArray();
        var hasMore = window.Length > query.Limit;
        var pageRows = window.Take(query.Limit).ToArray();
        var columns = DeploymentSchema
            .Select(column => new DatabaseColumnDescriptor(
                column.Name,
                column.DataTypeName,
                column.ValueKind,
                column.ClrTypeName,
                column.IsNullable,
                column.IsPrimaryKey,
                column.IsIdentity,
                !column.CanEdit,
                column.Name))
            .ToArray();
        var values = pageRows
            .Select(row => (IReadOnlyList<DatabaseValue>)ToValues(row))
            .ToArray();
        var displayRows = values
            .Select(row => (IReadOnlyList<string?>)row
                .Select(value => value.IsNull ? null : value.DisplayText)
                .ToArray())
            .ToArray();
        var result = new DatabaseQueryPage(
            columns,
            displayRows,
            hasMore,
            RowsAffected: 0,
            TimeSpan.FromMilliseconds(8),
            values);
        return Task.FromResult(new DatabaseTablePage(
            result,
            query.Offset,
            query.Limit,
            hasMore,
            filteredRows.LongLength));
    }

    public Task<DatabaseMutationResult> ApplyTableChangesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DatabaseMutationResult(
            changes.Inserts.Count,
            changes.Updates.Count,
            changes.Deletes.Count,
            Message: changes.IsEmpty ? "No changes to save." : "Changes saved."));
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
        $"SELECT * FROM \"{tableName}\" LIMIT {limit};";

    public string BuildInsertStatement(
        string driverId,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row) =>
        $"INSERT INTO \"{details.Object.Name}\" DEFAULT VALUES;";

    public DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString)
    {
        if (!connectionString.Contains('=', StringComparison.Ordinal))
        {
            return new DatabaseConnectionDetails(FilePath: connectionString);
        }

        var values = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        return new DatabaseConnectionDetails(
            values.GetValueOrDefault("Host"),
            values.TryGetValue("Port", out var port)
                && int.TryParse(port, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,
            values.GetValueOrDefault("Database"),
            values.GetValueOrDefault("Username"),
            values.GetValueOrDefault("Password"),
            Options: null);
    }

    public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
    {
        if (details.FilePath is { } filePath)
        {
            return filePath;
        }

        var pairs = new List<string>();
        Append("Host", details.Host);
        Append("Port", details.Port?.ToString(CultureInfo.InvariantCulture));
        Append("Database", details.Database);
        Append("Username", details.Username);
        Append("Password", details.Password);
        if (details.Options is { } options)
        {
            pairs.Add(options);
        }

        return string.Join(';', pairs);

        void Append(string key, string? value)
        {
            if (value is not null)
            {
                pairs.Add($"{key}={value}");
            }
        }
    }

    private static DateTimeOffset At(int hour, int minute, int second) =>
        new(2026, 8, 2, hour, minute, second, TimeSpan.Zero);

    private static DatabaseValue[] ToValues(QaDeployment row) =>
    [
        new(row.Id, DatabaseValueKind.SignedInteger, row.Id.ToString(CultureInfo.InvariantCulture)),
        new(row.Service, DatabaseValueKind.Text, row.Service),
        new(row.Region, DatabaseValueKind.Text, row.Region),
        new(row.Status, DatabaseValueKind.Text, row.Status ?? "NULL"),
        new(
            row.DeployedAt,
            DatabaseValueKind.TimestampWithZone,
            row.DeployedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
    ];

    private static bool Matches(QaDeployment row, DatabaseFilterCondition filter)
    {
        var current = ColumnValue(row, filter.ColumnName);
        if (filter.Operator == DatabaseFilterOperator.IsNull)
        {
            return current is null;
        }

        if (filter.Operator == DatabaseFilterOperator.IsNotNull)
        {
            return current is not null;
        }

        var comparison = Compare(current, filter.Value);
        var currentText = Convert.ToString(current, CultureInfo.InvariantCulture) ?? string.Empty;
        var expectedText = Convert.ToString(filter.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        return filter.Operator switch
        {
            DatabaseFilterOperator.Equal => comparison == 0,
            DatabaseFilterOperator.NotEqual => comparison != 0,
            DatabaseFilterOperator.LessThan => comparison < 0,
            DatabaseFilterOperator.LessThanOrEqual => comparison <= 0,
            DatabaseFilterOperator.GreaterThan => comparison > 0,
            DatabaseFilterOperator.GreaterThanOrEqual => comparison >= 0,
            DatabaseFilterOperator.Contains => currentText.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            DatabaseFilterOperator.StartsWith => currentText.StartsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            DatabaseFilterOperator.EndsWith => currentText.EndsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IReadOnlyList<QaDeployment> Sort(
        IEnumerable<QaDeployment> rows,
        IReadOnlyList<DatabaseSort> sorts)
    {
        var result = rows.ToList();
        if (sorts.Count == 0)
        {
            return result;
        }

        result.Sort((left, right) =>
        {
            foreach (var sort in sorts)
            {
                var comparison = Compare(
                    ColumnValue(left, sort.ColumnName),
                    ColumnValue(right, sort.ColumnName));
                if (comparison != 0)
                {
                    return sort.Descending ? -comparison : comparison;
                }
            }

            return 0;
        });
        return result;
    }

    private static object? ColumnValue(QaDeployment row, string columnName) =>
        columnName.ToLowerInvariant() switch
        {
            "id" => row.Id,
            "service" => row.Service,
            "region" => row.Region,
            "status" => row.Status,
            "deployed_at" => row.DeployedAt,
            _ => null,
        };

    private static int Compare(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null ? right is null ? 0 : -1 : 1;
        }

        if (left is long integer
            && long.TryParse(
                Convert.ToString(right, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                out var expectedInteger))
        {
            return integer.CompareTo(expectedInteger);
        }

        if (left is DateTimeOffset timestamp
            && DateTimeOffset.TryParse(
                Convert.ToString(right, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expectedTimestamp))
        {
            return timestamp.CompareTo(expectedTimestamp);
        }

        return string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record QaDeployment(
        long Id,
        string Service,
        string Region,
        string? Status,
        DateTimeOffset DeployedAt);
}

/// <summary>
/// Layout-only stand-in for the statistics view model: real presentation values
/// with no monitoring runtime behind them, so the panel's card layout is
/// capturable at any width.
/// </summary>
internal sealed class QaStatisticsPreview
{
    public bool IsActive => false;

    public bool ShowContent => true;

    public bool IsVisibleInLayout => true;

    public string StatusText => "Live · Local";

    public IBrush StatusColor => Brushes.MediumSeaGreen;

    public string ConnectionDisplayName => "Local";

    public string CpuText => "3.8%";

    public string MemoryText => "101.9 GiB";

    public string ProcessCountText => "1 180";

    public string ProcessDetailText => "Resource details available for all processes";

    public string UptimeText => "9d 5h 30m";

    public int ProcessorCountText => 16;

    public bool HasIssue => false;

    public bool ShowLoading => false;

    public bool ShowTerminalError => false;

    public string CapturedAtText => "Captured 09:30:00";

    public string IssueTitle => string.Empty;

    public string IssueMessage => string.Empty;

    public IReadOnlyList<double> CpuHistory { get; } =
        [4.1, 3.6, 5.2, 3.9, 4.4, 3.2, 3.8, 4.9, 3.5, 3.8];

    public IReadOnlyList<double> MemoryHistory { get; } =
        [101.2, 101.4, 101.3, 101.6, 101.5, 101.8, 101.7, 101.9];
}

/// <summary>
/// Layout-only stand-in for the process monitor view model, with names long
/// enough to prove the name column trims instead of overlapping its neighbours.
/// </summary>
internal sealed class QaProcessMonitorPreview
{
    public sealed record Row(
        int ProcessId,
        string Name,
        string Cpu,
        string Memory,
        string Started,
        bool IsGhostShell)
    {
        public string AccessibleSummary =>
            $"PID {ProcessId}, {Name}, CPU {Cpu}, memory {Memory}, started {Started}.";
    }

    public bool IsActive => false;

    public bool IsVisibleInLayout => true;

    public bool ShowContent => true;

    public bool ShowLoading => false;

    public bool ShowTerminalError => false;

    public bool ShowInlineIssue => false;

    public string StatusText => "Live · Local";

    public IBrush StatusColor => Brushes.MediumSeaGreen;

    public string ConnectionDisplayName => "Local";

    public string Filter => string.Empty;

    public IReadOnlyList<string> SortOptions { get; } = ["Cpu descending"];

    public string Sort => "Cpu descending";

    public string IssueTitle => string.Empty;

    public string IssueMessage => string.Empty;

    public string ShowingText => "250 processes";

    public string CapturedAtText => "Captured 18:30:53";

    public IReadOnlyList<Row> Processes { get; } =
    [
        new(96971, "GhostShell", "0.8%", "562.4 MiB", "03.08.2026 18:26", true),
        new(61335, "Brave Browser Helper (Renderer)", "0.2%", "214.5 MiB", "01.08.2026 22:55", false),
        new(37330, "AMSUIPaymentsViewService_macOS", "0.0%", "15.0 MiB", "31.07.2026 14:09", false),
        new(49345, "BackgroundTaskManagementAgent", "0.0%", "28.1 MiB", "01.08.2026 22:46", false),
        new(51129, "sysmond", "0.2%", "5.3 MiB", "01.08.2026 22:48", false),
        new(17880, "Signal", "0.0%", "269.1 MiB", "31.07.2026 11:39", false),
        new(908, "bridge-gui", "0.0%", "90.7 MiB", "25.07.2026 12:52", false),
    ];

    public Row? SelectedProcess { get; set; }
}
