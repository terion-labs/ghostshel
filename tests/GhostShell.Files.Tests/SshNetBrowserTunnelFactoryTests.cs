using GhostShell.Core;

namespace GhostShell.Files.Tests;

public sealed class SshNetBrowserTunnelFactoryTests
{
    [Fact]
    public async Task Non_ssh_connections_are_rejected_before_credentials_are_used()
    {
        var factory = new SshNetBrowserTunnelFactory(null!, null!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(BuiltInConnections.Local, CancellationToken.None));

        Assert.Contains("not an SSH connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_routes_require_an_explicit_username()
    {
        var factory = new SshNetBrowserTunnelFactory(null!, null!);
        var connection = new ConnectionProfile(
            new ConnectionId("browser-route"),
            ConnectionProfile.CurrentSchemaVersion,
            "Browser route",
            new ConnectionEndpoint.Ssh("bastion.example.test"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(connection, CancellationToken.None));

        Assert.Contains("explicit username", exception.Message, StringComparison.Ordinal);
    }
}
