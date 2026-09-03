using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class WorkspaceNetworkContractsTests
{
    private static readonly NetworkConnectionId ConnectionId = new("network-contract");

    [Theory]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://127.0.0.1:8443")]
    public void Proxy_egress_accepts_supported_credential_free_endpoints(string endpoint)
    {
        var egress = WorkspaceNetworkEgress.ViaProxy(new Uri(endpoint));

        Assert.Equal(new Uri(endpoint), egress.ProxyEndpoint);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("socks5://alice:secret@127.0.0.1:1080")]
    [InlineData("relative")]
    public void Proxy_egress_rejects_unsupported_or_credential_bearing_endpoints(string endpoint)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            WorkspaceNetworkEgress.ViaProxy(new Uri(endpoint, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void Policy_update_requires_every_referenced_profile()
    {
        var policy = new NetworkPolicy([ConnectionId], ConnectionId, true, true);

        _ = Assert.Throws<ArgumentException>(() => new WorkspaceNetworkPolicyUpdate(policy, []));
    }

    [Fact]
    public void Snapshot_rejects_contradictory_state()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Direct,
                WorkspaceNetworkEgress.Blocked,
                null));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connected,
                WorkspaceNetworkEgress.Attached,
                null));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Failed,
                WorkspaceNetworkEgress.Direct,
                ConnectionId));
    }

    [Fact]
    public void Provider_contract_receives_the_workspace_placement()
    {
        var profile = Profile();
        var request = new NetworkConnectionStartRequest(
            new WorkspaceInstanceId("running-workspace"),
            profile,
            WorkspaceNetworkPlacement.Host,
            killSwitchEnabled: true);

        Assert.Same(profile, request.Connection);
        Assert.IsType<WorkspaceNetworkPlacement.HostPlacement>(request.Placement);
        Assert.True(request.KillSwitchEnabled);
    }

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
}
