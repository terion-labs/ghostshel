using System.Text;
using GhostShell.Application;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure.Tests;

/// <summary>
/// Application encryption against the real engine and a real database file:
/// enabling converts the disk, the keys live in the vault, and a restart
/// finds everything exactly as the switch left it.
/// </summary>
public sealed class ApplicationEncryptionRuntimeTests : IAsyncDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ghostshell-app-encryption").FullName;

    private readonly PersistentTestVault _vault = new();

    /// <summary>
    /// The in-memory vault presenting as OS-protected: encryption rightly
    /// refuses a keystore whose keys die with the process, and these tests
    /// are about the conversion, not the keystore.
    /// </summary>
    private sealed class PersistentTestVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public SecretVaultAvailability Availability => new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            SecretVaultCapabilities.All,
            "test",
            "test_persistent",
            "Test vault presenting as persistent.");

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.CreateAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.ResolveAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) =>
            _inner.ReplaceAsync(request, material, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.RelabelAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(request, cancellationToken);

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            _inner.ListMetadataAsync(request, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
    private readonly List<GhostShellDatabase> _databases = [];

    private string DatabasePath => Path.Combine(_root, "ghostshell.db");

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }

        _vault.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Enabling_encrypts_the_database_in_place_and_a_restart_still_opens_it()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);

        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        Assert.True(runtime.IsEnabled);
        Assert.False(LooksLikePlainSqlite(), "The database still announces itself as plain SQLite.");
        var image = await File.ReadAllBytesAsync(DatabasePath);
        Assert.True(
            image.AsSpan().IndexOf("sentinel-value"u8) < 0,
            "The sentinel row is readable in the encrypted database file.");
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));

        // A restart: a fresh runtime over the same disk and vault.
        var (rebooted, rebootedDatabase) = Compose();
        await rebooted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.True(rebooted.IsEnabled);
        Assert.Null(rebooted.StartupError);
        Assert.Equal("sentinel-value", await ReadSentinelAsync(rebootedDatabase));
    }

    [Fact]
    public async Task Disabling_decrypts_in_place_and_forgets_the_keys()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        Assert.Null(await runtime.SetEnabledAsync(false, CancellationToken.None));

        Assert.False(runtime.IsEnabled);
        Assert.True(LooksLikePlainSqlite());
        Assert.Equal("sentinel-value", await ReadSentinelAsync(database));
        // The keys are gone: a fresh runtime sees a plain database and has
        // nothing to resolve.
        var (rebooted, _) = Compose();
        await rebooted.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);
        Assert.False(rebooted.IsEnabled);
        Assert.Null(rebooted.PersistentCachePassword);
    }

    [Fact]
    public async Task An_encrypted_database_whose_key_is_gone_is_a_startup_error_not_a_crash()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));

        // The keystore is lost — a new vault knows nothing.
        using var emptyVault = new PersistentTestVault();
        var orphaned = new ApplicationEncryptionRuntime(
            emptyVault,
            DatabasePath,
            () => throw new InvalidOperationException("The database must not be touched."));
        await orphaned.InitializeAsync(wrappedKeysPending: false, CancellationToken.None);

        Assert.True(orphaned.IsEnabled);
        Assert.NotNull(orphaned.StartupError);
    }

    [Fact]
    public async Task The_cache_password_exists_exactly_while_encryption_is_enabled()
    {
        var (runtime, database) = Compose();
        await WriteSentinelAsync(database);
        Assert.Null(runtime.PersistentCachePassword);

        Assert.Null(await runtime.SetEnabledAsync(true, CancellationToken.None));
        Assert.NotNull(runtime.PersistentCachePassword);

        Assert.Null(await runtime.SetEnabledAsync(false, CancellationToken.None));
        Assert.Null(runtime.PersistentCachePassword);
    }

    private (ApplicationEncryptionRuntime Runtime, GhostShellDatabase Database) Compose()
    {
        ApplicationEncryptionRuntime? runtime = null;
        var options = new SqliteStorageOptions(
            DatabasePath,
            acquireProfileLock: false)
        {
            PasswordProvider = () => runtime!.ConfigDatabasePassword,
        };
        var database = new GhostShellDatabase(options, TimeProvider.System);
        _databases.Add(database);
        runtime = new ApplicationEncryptionRuntime(_vault, DatabasePath, () => database);
        return (runtime, database);
    }

    private static async Task WriteSentinelAsync(GhostShellDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_restore_preference SET restore_sessions_on_start = 1;
            INSERT INTO definitions(
                kind, id, schema_version, revision, name, payload_json,
                created_utc, updated_utc)
            VALUES (
                'probe', 'sentinel', 1, 1, 'sentinel-value', '{}',
                '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadSentinelAsync(GhostShellDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM definitions WHERE id = 'sentinel';";
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private bool LooksLikePlainSqlite()
    {
        using var file = File.OpenRead(DatabasePath);
        var header = new byte[15];
        file.ReadExactly(header);
        return Encoding.ASCII.GetString(header) == "SQLite format 3";
    }
}
