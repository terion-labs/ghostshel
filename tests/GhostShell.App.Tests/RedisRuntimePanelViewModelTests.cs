using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class RedisRuntimePanelViewModelTests
{
    [Fact]
    public async Task Restored_saved_connection_waits_for_connect_before_resolving_credential()
    {
        var secret = SecretRef.New();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "redis",
            RedisDatabase.DriverId,
            "localhost:6379",
            secret);
        var resolveCount = 0;
        using var panel = new RedisRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Redis",
            new RedisPanelFixtures.StubFactory(
                new RedisPanelFixtures.StubSession(timeToLive: null)),
            new RedisPanelFixtures.StubCatalog(),
            savedConnection: profile,
            passwordResolver: (reference, _) =>
            {
                Assert.Equal(secret, reference);
                resolveCount++;
                return Task.FromResult<string?>("vaulted");
            },
            deferStoredCredentialAccess: true);

        await panel.Initialization;

        Assert.Equal(0, resolveCount);
        Assert.False(panel.IsConnected);

        await panel.ConnectAsync();

        Assert.Equal(1, resolveCount);
        Assert.True(panel.IsConnected);
    }
}
