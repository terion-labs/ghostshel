using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure;

/// <summary>
/// Application encryption over SQLite3 Multiple Ciphers and the OS keystore.
///
/// The configuration database is re-keyed in place; the keys are random,
/// generated here, and stored only in the operating system's keystore under
/// fixed references. What is enabled is read from the file itself — a plain
/// SQLite database announces itself in its first sixteen bytes, an encrypted
/// one is indistinguishable from noise — so the setting can never disagree
/// with the state of the disk.
/// </summary>
public sealed class ApplicationEncryptionRuntime : IApplicationEncryption, IDisposable
{
    private static readonly SecretRef ConfigKeyReference = new("app.security.config-database-key");
    private static readonly SecretRef CacheKeyReference = new("app.security.preview-cache-key");

    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.PlatformMaintenance,
        SecretUsePurpose.GlobalTargetId);

    private const string PlainHeader = "SQLite format 3\0";

    private readonly ISecretVault _vault;
    private readonly bool _ownsVault;
    private readonly string _databasePath;
    private readonly Func<GhostShellDatabase> _database;
    private string? _configPassword;
    private string? _cachePassword;
    private bool _enabled;

    public ApplicationEncryptionRuntime(
        ISecretVault vault,
        string databasePath,
        Func<GhostShellDatabase> database,
        bool ownsVault = false)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _ownsVault = ownsVault;
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public void Dispose()
    {
        if (_ownsVault)
        {
            _vault.Dispose();
        }
    }

    public bool IsSupported => _vault.Availability.CanPersist;

    public bool IsEnabled => _enabled;

    public string? UnsupportedReason => IsSupported
        ? null
        : "Application encryption needs the operating system's keystore, which is unavailable: "
            + _vault.Availability.Message;

    /// <summary>
    /// What startup could not do, when the disk and the keystore disagree —
    /// an encrypted database whose key is gone cannot be opened, and saying
    /// so beats failing on the first query.
    /// </summary>
    public string? StartupError { get; private set; }

    public event EventHandler? Changed;

    public string? PersistentCachePassword => _cachePassword;

    /// <summary>
    /// The password the configuration database opens with right now. Read by
    /// the storage options' password provider on every connection build.
    /// </summary>
    public string? ConfigDatabasePassword => _configPassword;

    /// <summary>
    /// True while the database is encrypted and its keys wait behind the
    /// startup PIN: nothing that needs the database may run until
    /// <see cref="AcceptUnwrappedKeys"/> delivers them.
    /// </summary>
    public bool AwaitingUnlock { get; private set; }

    /// <summary>
    /// Reads the state of the disk and, when encrypted, the keys from the
    /// keystore. Runs before anything opens the configuration database.
    /// <paramref name="wrappedKeysPending"/> says startup protection holds
    /// the keys sealed under the PIN — then their absence from the keystore
    /// is the design working, not a loss.
    /// </summary>
    public async ValueTask InitializeAsync(
        bool wrappedKeysPending,
        CancellationToken cancellationToken)
    {
        if (!DatabaseLooksEncrypted())
        {
            return;
        }

        _enabled = true;
        _configPassword = await ResolveAsync(ConfigKeyReference, cancellationToken)
            .ConfigureAwait(false);
        if (_configPassword is null && wrappedKeysPending)
        {
            AwaitingUnlock = true;
            return;
        }

        if (_configPassword is null)
        {
            StartupError =
                "The configuration database is encrypted but its key is not in the OS keystore. "
                + "Restore the keystore entry, or delete the database to start over.";
            return;
        }

        _cachePassword = await ResolveAsync(CacheKeyReference, cancellationToken)
            .ConfigureAwait(false);
        if (_cachePassword is null)
        {
            // The cache is disposable, so a lost cache key costs a re-download,
            // not data. A fresh key means a fresh container.
            _cachePassword = NewKey();
            await StoreAsync(
                    CacheKeyReference,
                    "Preview cache encryption key",
                    _cachePassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<string?> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (enabled == _enabled)
        {
            return null;
        }

        if (enabled && !IsSupported)
        {
            return UnsupportedReason;
        }

        return enabled
            ? await EnableAsync(cancellationToken).ConfigureAwait(false)
            : await DisableAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string?> EnableAsync(CancellationToken cancellationToken)
    {
        var configKey = NewKey();
        var cacheKey = NewKey();
        if (!await StoreAsync(
                    ConfigKeyReference,
                    "Configuration database encryption key",
                    configKey,
                    cancellationToken)
                .ConfigureAwait(false)
            || !await StoreAsync(
                    CacheKeyReference,
                    "Preview cache encryption key",
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
            return "The OS keystore refused to store the encryption keys.";
        }

        try
        {
            await RekeyAsync(currentPassword: null, configKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
            return $"The configuration database could not be encrypted: {exception.Message}";
        }

        _configPassword = configKey;
        _cachePassword = cacheKey;
        _enabled = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// The keys as startup protection seals them. Null while disabled — there
    /// is nothing to seal.
    /// </summary>
    internal (string Config, string Cache)? ExportKeys() =>
        _enabled && _configPassword is not null && _cachePassword is not null
            ? (_configPassword, _cachePassword)
            : null;

    /// <summary>
    /// Removes the keystore copies while keeping the keys in memory: from now
    /// on the sealed blob under the PIN is their only durable home.
    /// </summary>
    internal ValueTask ForgetKeystoreCopiesAsync(CancellationToken cancellationToken) =>
        ForgetAsync(cancellationToken);

    /// <summary>Puts the keys back into the keystore — protection turned off.</summary>
    internal async ValueTask<bool> RestoreKeystoreCopiesAsync(
        string configKey,
        string cacheKey,
        CancellationToken cancellationToken) =>
        await StoreAsync(
                ConfigKeyReference,
                "Configuration database encryption key",
                configKey,
                cancellationToken)
            .ConfigureAwait(false)
        && await StoreAsync(
                CacheKeyReference,
                "Preview cache encryption key",
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Startup protection verified the PIN and unsealed the keys. From here
    /// the runtime behaves exactly as if the keystore had answered.
    /// </summary>
    internal void AcceptUnwrappedKeys(string configKey, string cacheKey)
    {
        _configPassword = configKey;
        _cachePassword = cacheKey;
        _enabled = true;
        AwaitingUnlock = false;
        StartupError = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async ValueTask<string?> DisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RekeyAsync(_configPassword, newPassword: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            return $"The configuration database could not be decrypted: {exception.Message}";
        }

        _configPassword = null;
        _cachePassword = null;
        _enabled = false;
        await ForgetAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Re-keys the database in place under the exclusive-maintenance gate.
    /// The engine refuses to rekey in WAL mode, so the journal is dropped to
    /// rollback for the conversion and restored after; the change is then
    /// verified by opening with the new password before anything else may.
    /// </summary>
    private async Task RekeyAsync(
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        await _database().RunExclusiveMaintenanceAsync(
                async token =>
                {
                    await using (var connection = OpenRaw(currentPassword))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await ExecuteAsync(
                                connection,
                                "PRAGMA journal_mode=DELETE;"
                                + $"PRAGMA rekey='{newPassword ?? string.Empty}';"
                                + "PRAGMA journal_mode=WAL;",
                                token)
                            .ConfigureAwait(false);
                    }

                    await using var verification = OpenRaw(newPassword);
                    await verification.OpenAsync(token).ConfigureAwait(false);
                    await using var integrity = verification.CreateCommand();
                    integrity.CommandText = "PRAGMA integrity_check;";
                    var verdict = Convert.ToString(
                        await integrity.ExecuteScalarAsync(token).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SqliteException(
                            "The re-keyed database failed integrity validation.",
                            26);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private SqliteConnection OpenRaw(string? password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        if (password is not null)
        {
            builder.Password = password;
        }

        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool DatabaseLooksEncrypted()
    {
        try
        {
            using var file = new FileStream(
                _databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var header = new byte[PlainHeader.Length];
            var read = file.Read(header);
            return read == header.Length
                && !Encoding.ASCII.GetString(header).Equals(PlainHeader, StringComparison.Ordinal);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // No database yet: nothing is encrypted until the setting says so.
            return false;
        }
    }

    /// <summary>
    /// A key as SQLite's PRAGMA and LiteDB's connection string both accept it
    /// verbatim: hex spells no quotes, no escapes, and no surprises.
    /// </summary>
    private static string NewKey() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private async ValueTask<string?> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var resolved = await _vault.ResolveAsync(
                new ResolveSecretRequest(reference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not SecretVaultResult<SecretMaterial>.Success success)
        {
            return null;
        }

        using var material = success.Value;
        var buffer = new byte[material.Length];
        material.CopyTo(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private async ValueTask<bool> StoreAsync(
        SecretRef reference,
        string label,
        string key,
        CancellationToken cancellationToken)
    {
        // Deleted first so a stale entry from an earlier run cannot make the
        // create fail as a duplicate; a missing entry is not an error here.
        await _vault.DeleteAsync(
                new DeleteSecretRequest(reference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(key));
        var created = await _vault.CreateAsync(
                new CreateSecretRequest(
                    reference,
                    label,
                    SecretKind.Other,
                    SecretScope.Global,
                    Purpose),
                material,
                cancellationToken)
            .ConfigureAwait(false);
        return created is SecretVaultResult<SecretMetadata>.Success;
    }

    private async ValueTask ForgetAsync(CancellationToken cancellationToken)
    {
        foreach (var reference in new[] { ConfigKeyReference, CacheKeyReference })
        {
            await _vault.DeleteAsync(
                    new DeleteSecretRequest(reference, SecretScope.Global, Purpose),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
