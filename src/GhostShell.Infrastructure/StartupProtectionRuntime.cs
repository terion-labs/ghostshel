using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Startup protection over a peppered PIN verifier.
///
/// What is on disk is a salt, an iteration count and a PBKDF2 verifier —
/// derived from the PIN <em>and</em> a random pepper that exists only in the
/// OS keystore. Testing guesses therefore needs both the file and the
/// keystore; the file alone, lifted from a backup, verifies nothing. Wrong
/// guesses are counted in the same file, and past a small allowance each
/// further attempt waits twice as long as the one before — a restart does
/// not reset the meter, because the meter is on disk.
/// </summary>
public sealed class StartupProtectionRuntime : IStartupProtection
{
    private const string FileName = "startup-protection.json";
    private const int Iterations = 600_000;
    private const int FreeAttempts = 5;

    private static readonly SecretRef PepperReference = new("app.security.startup-pepper");

    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.PlatformMaintenance,
        SecretUsePurpose.GlobalTargetId);

    private readonly object _gate = new();
    private readonly ISecretVault _vault;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private ProtectionFile? _state;
    private bool _locked;

    public StartupProtectionRuntime(
        ISecretVault vault,
        string dataDirectory,
        TimeProvider? timeProvider = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(Path.GetFullPath(dataDirectory), FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = Read();
        _locked = _state is not null;
    }

    public bool IsEnabled => _state is not null;

    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    public TimeSpan? LockTimeout => _state?.LockTimeoutSeconds is > 0
        ? TimeSpan.FromSeconds(_state.LockTimeoutSeconds.Value)
        : null;

    public int RetryDelaySeconds
    {
        get
        {
            lock (_gate)
            {
                if (_state?.RetryAfterUtc is not { } retryAfter)
                {
                    return 0;
                }

                var wait = retryAfter - _timeProvider.GetUtcNow();
                return wait > TimeSpan.Zero ? (int)Math.Ceiling(wait.TotalSeconds) : 0;
            }
        }
    }

    public event EventHandler? Changed;

    public async ValueTask<string?> EnableAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        if (Requirement(pin) is { } refused)
        {
            return refused;
        }

        if (!_vault.Availability.CanPersist)
        {
            return "Startup protection needs the operating system's keystore, which is "
                + "unavailable: " + _vault.Availability.Message;
        }

        var pepper = await ResolveOrCreatePepperAsync(cancellationToken).ConfigureAwait(false);
        if (pepper is null)
        {
            return "The OS keystore refused to store the protection pepper.";
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var file = new ProtectionFile(
            Version: 1,
            Salt: Convert.ToHexStringLower(salt),
            Iterations,
            Verifier: Convert.ToHexStringLower(Derive(pin, salt, pepper)),
            LockTimeoutSeconds: _state?.LockTimeoutSeconds,
            FailedAttempts: 0,
            RetryAfterUtc: null);
        lock (_gate)
        {
            _state = file;
            Write(file);
            // Enabling is itself an authenticated moment; the session stays
            // unlocked until the timeout or an explicit lock.
            _locked = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public async ValueTask<string?> DisableAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        if (_state is null)
        {
            return null;
        }

        if (!await TryUnlockAsync(pin, cancellationToken).ConfigureAwait(false))
        {
            return RetryDelaySeconds > 0
                ? $"That PIN was refused; the next attempt can be made in {RetryDelaySeconds}s."
                : "That PIN was refused.";
        }

        lock (_gate)
        {
            _state = null;
            _locked = false;
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        await _vault.DeleteAsync(
                new DeleteSecretRequest(PepperReference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public async ValueTask<bool> TryUnlockAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        var state = _state;
        if (state is null)
        {
            return true;
        }

        lock (_gate)
        {
            if (state.RetryAfterUtc is { } retryAfter
                && retryAfter > _timeProvider.GetUtcNow())
            {
                return false;
            }
        }

        var pepper = await ResolvePepperAsync(cancellationToken).ConfigureAwait(false);
        if (pepper is null)
        {
            // Without the pepper nothing can verify; refusing every PIN would
            // lock the user out of their own machine's data for want of a
            // keystore glitch. The pepper is the brute-force defense, not the
            // authentication itself.
            return false;
        }

        var offered = Derive(pin ?? string.Empty, Convert.FromHexString(state.Salt), pepper);
        var expected = Convert.FromHexString(state.Verifier);
        var matches = CryptographicOperations.FixedTimeEquals(offered, expected);
        lock (_gate)
        {
            if (matches)
            {
                _locked = false;
                if (state.FailedAttempts != 0 || state.RetryAfterUtc is not null)
                {
                    _state = state with { FailedAttempts = 0, RetryAfterUtc = null };
                    Write(_state);
                }
            }
            else
            {
                var attempts = state.FailedAttempts + 1;
                // 30s after the free allowance, doubling per miss, capped at
                // an hour: enough to make guessing hopeless, never enough to
                // lock the owner out for good.
                var delay = attempts <= FreeAttempts
                    ? (TimeSpan?)null
                    : TimeSpan.FromSeconds(Math.Min(
                        3600,
                        30 * Math.Pow(2, Math.Min(attempts - FreeAttempts - 1, 7))));
                _state = state with
                {
                    FailedAttempts = attempts,
                    RetryAfterUtc = delay is null
                        ? null
                        : _timeProvider.GetUtcNow() + delay,
                };
                Write(_state);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return matches;
    }

    public void UnlockAuthenticated()
    {
        lock (_gate)
        {
            if (_state is null || !_locked)
            {
                return;
            }

            _locked = false;
            if (_state.FailedAttempts != 0 || _state.RetryAfterUtc is not null)
            {
                // The person is verified; the miss meter has nothing left to
                // defend against.
                _state = _state with { FailedAttempts = 0, RetryAfterUtc = null };
                Write(_state);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Lock()
    {
        lock (_gate)
        {
            if (_state is null || _locked)
            {
                return;
            }

            _locked = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask SetLockTimeoutAsync(
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return ValueTask.CompletedTask;
            }

            _state = _state with
            {
                LockTimeoutSeconds = timeout is { } value && value > TimeSpan.Zero
                    ? (long)value.TotalSeconds
                    : null,
            };
            Write(_state);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    private static string? Requirement(string pin) =>
        string.IsNullOrEmpty(pin) || pin.Trim().Length < 4
            ? "A PIN needs at least four characters."
            : null;

    private static byte[] Derive(string pin, byte[] salt, byte[] pepper)
    {
        // The pepper joins the salt rather than the PIN so a caller can never
        // weaken it by formatting; both are byte-appended, no encoding games.
        var seasoned = new byte[salt.Length + pepper.Length];
        salt.CopyTo(seasoned, 0);
        pepper.CopyTo(seasoned, salt.Length);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            seasoned,
            Iterations,
            HashAlgorithmName.SHA512,
            outputLength: 32);
    }

    private async ValueTask<byte[]?> ResolvePepperAsync(CancellationToken cancellationToken)
    {
        var resolved = await _vault.ResolveAsync(
                new ResolveSecretRequest(PepperReference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not SecretVaultResult<SecretMaterial>.Success success)
        {
            return null;
        }

        using var material = success.Value;
        var pepper = new byte[material.Length];
        material.CopyTo(pepper);
        return pepper;
    }

    private async ValueTask<byte[]?> ResolveOrCreatePepperAsync(
        CancellationToken cancellationToken)
    {
        if (await ResolvePepperAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var pepper = RandomNumberGenerator.GetBytes(32);
        using var material = SecretMaterial.CopyFrom(pepper);
        var created = await _vault.CreateAsync(
                new CreateSecretRequest(
                    PepperReference,
                    "Startup protection pepper",
                    SecretKind.Other,
                    SecretScope.Global,
                    Purpose),
                material,
                cancellationToken)
            .ConfigureAwait(false);
        return created is SecretVaultResult<SecretMetadata>.Success ? pepper : null;
    }

    private ProtectionFile? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<ProtectionFile>(File.ReadAllText(_path));
        }
        catch (Exception exception) when (exception
            is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    private void Write(ProtectionFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(file));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // The attempt meter degrades to per-run; the verifier itself was
            // already read, so unlocking still works.
        }
    }

    /// <summary>Everything on disk. No secrets: the pepper never joins it.</summary>
    private sealed record ProtectionFile(
        int Version,
        string Salt,
        int Iterations,
        string Verifier,
        long? LockTimeoutSeconds,
        int FailedAttempts,
        DateTimeOffset? RetryAfterUtc);
}
