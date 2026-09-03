namespace GhostShell.Application;

/// <summary>
/// Opens outbound TCP streams through the network route currently selected for
/// one live workspace. Consumers resolve the route for every new connection so
/// a proxy change or kill switch takes effect without rebuilding panel clients.
/// </summary>
public interface IWorkspaceNetworkConnector
{
    WorkspaceNetworkEgress Egress { get; }

    /// <summary>
    /// Stable loopback SOCKS5 endpoint for libraries that own their socket
    /// creation but support a proxy. It remains stable when the selected route
    /// changes and enforces the current kill-switch state.
    /// </summary>
    Uri LocalProxyEndpoint { get; }

    ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceNetworkBlockedException : IOException
{
    public WorkspaceNetworkBlockedException()
        : base("The workspace network kill switch is blocking traffic.")
    {
    }
}
