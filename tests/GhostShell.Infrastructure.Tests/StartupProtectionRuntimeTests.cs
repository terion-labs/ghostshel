using GhostShell.Application;

namespace GhostShell.Infrastructure.Tests;

/// <summary>
/// The PIN gate: verification is peppered, misses meter into a persisted
/// doubling delay, and a fresh process finds the gate exactly as the last
/// one left it.
/// </summary>
public sealed class StartupProtectionRuntimeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ghostshell-startup-protection").FullName;

    private readonly PersistentVault _vault = new();
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-08-04T10:00:00Z"));

    public void Dispose()
    {
        _vault.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private StartupProtectionRuntime Create() => new(_vault, _root, _clock);

    [Fact]
    public async Task Enabling_locks_future_starts_and_the_right_pin_unlocks()
    {
        var first = Create();
        Assert.False(first.IsEnabled);
        Assert.False(first.IsLocked);

        Assert.Null(await first.EnableAsync("4812", CancellationToken.None));
        Assert.True(first.IsEnabled);
        // The person who just chose the PIN is not asked to repeat it.
        Assert.False(first.IsLocked);

        // A fresh start finds the gate down.
        var restarted = Create();
        Assert.True(restarted.IsEnabled);
        Assert.True(restarted.IsLocked);
        Assert.False(await restarted.TryUnlockAsync("0000", CancellationToken.None));
        Assert.True(restarted.IsLocked);
        Assert.True(await restarted.TryUnlockAsync("4812", CancellationToken.None));
        Assert.False(restarted.IsLocked);
    }

    [Fact]
    public async Task A_short_pin_is_refused()
    {
        var runtime = Create();
        Assert.NotNull(await runtime.EnableAsync("12", CancellationToken.None));
        Assert.False(runtime.IsEnabled);
    }

    [Fact]
    public async Task Repeated_misses_earn_a_persisted_doubling_delay()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        runtime.Lock();

        for (var miss = 0; miss < 5; miss++)
        {
            Assert.False(await runtime.TryUnlockAsync("0000", CancellationToken.None));
            Assert.Equal(0, runtime.RetryDelaySeconds);
        }

        // The sixth miss starts the meter.
        Assert.False(await runtime.TryUnlockAsync("0000", CancellationToken.None));
        Assert.Equal(30, runtime.RetryDelaySeconds);

        // Even the right PIN waits out the delay.
        Assert.False(await runtime.TryUnlockAsync("4812", CancellationToken.None));

        // A restart does not reset the meter: it is on disk.
        var restarted = Create();
        Assert.True(restarted.RetryDelaySeconds > 0);

        // Waiting does.
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, restarted.RetryDelaySeconds);
        Assert.True(await restarted.TryUnlockAsync("4812", CancellationToken.None));
        Assert.Equal(0, restarted.RetryDelaySeconds);
    }

    [Fact]
    public async Task The_file_alone_verifies_nothing_without_the_keystore_pepper()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));

        // The protection file survives; the keystore does not.
        using var strangerVault = new PersistentVault();
        var stranger = new StartupProtectionRuntime(strangerVault, _root, _clock);
        Assert.True(stranger.IsEnabled);
        Assert.False(await stranger.TryUnlockAsync("4812", CancellationToken.None));
    }

    [Fact]
    public async Task Disabling_requires_the_pin_and_removes_the_gate()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));

        Assert.NotNull(await runtime.DisableAsync("0000", CancellationToken.None));
        Assert.True(runtime.IsEnabled);

        Assert.Null(await runtime.DisableAsync("4812", CancellationToken.None));
        Assert.False(runtime.IsEnabled);
        Assert.False(Create().IsEnabled);
    }

    [Fact]
    public async Task The_lock_timeout_is_kept_with_the_gate()
    {
        var runtime = Create();
        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        await runtime.SetLockTimeoutAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), Create().LockTimeout);
    }

    [Fact]
    public async Task Every_state_change_is_announced()
    {
        var runtime = Create();
        var announcements = 0;
        runtime.Changed += (_, _) => announcements++;

        Assert.Null(await runtime.EnableAsync("4812", CancellationToken.None));
        runtime.Lock();
        Assert.True(await runtime.TryUnlockAsync("4812", CancellationToken.None));
        await runtime.SetLockTimeoutAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(await runtime.DisableAsync("4812", CancellationToken.None));

        // Enable, lock, unlock, timeout, the disable's own unlock, disable.
        Assert.True(announcements >= 5);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The in-memory vault presenting as OS-protected.</summary>
    private sealed class PersistentVault : ISecretVault
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
}
