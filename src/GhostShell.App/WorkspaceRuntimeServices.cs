using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;
using GhostShell.Git;

namespace GhostShell.App;

/// <summary>
/// The process and panel backends selected for one running workspace. Both
/// direct and isolated workspaces pass through this surface so workspace
/// networking can decorate either route without leaking platform policy into
/// the presentation layer.
/// </summary>
public sealed record WorkspaceRuntimeBackends
{
    public WorkspaceRuntimeBackends(
        IDockerEngineClient? dockerEngineClient,
        IGitRepositoryClient? gitRepositoryClient,
        IFilePanelClient filePanelClient,
        IFileTransferQueueClient? fileTransferQueueClient,
        IDatabasePanelClient? databasePanelClient,
        IRedisPanelSessionFactory? redisPanelSessionFactory,
        IBrowserRendererViewFactory? browserRendererViewFactory)
    {
        DockerEngineClient = dockerEngineClient;
        GitRepositoryClient = gitRepositoryClient;
        FilePanelClient = filePanelClient
            ?? throw new ArgumentNullException(nameof(filePanelClient));
        FileTransferQueueClient = fileTransferQueueClient;
        DatabasePanelClient = databasePanelClient;
        RedisPanelSessionFactory = redisPanelSessionFactory;
        BrowserRendererViewFactory = browserRendererViewFactory;
    }

    public IDockerEngineClient? DockerEngineClient { get; }

    public IGitRepositoryClient? GitRepositoryClient { get; }

    public IFilePanelClient FilePanelClient { get; }

    public IFileTransferQueueClient? FileTransferQueueClient { get; }

    public IDatabasePanelClient? DatabasePanelClient { get; }

    public IRedisPanelSessionFactory? RedisPanelSessionFactory { get; }

    public IBrowserRendererViewFactory? BrowserRendererViewFactory { get; }
}

public abstract record WorkspaceNetworkRoute
{
    private WorkspaceNetworkRoute()
    {
    }

    public static WorkspaceNetworkRoute Direct { get; } = new DirectRoute();

    public virtual Uri? ProxyUri => null;

    public static WorkspaceNetworkRoute ViaProxy(Uri proxyUri)
    {
        ArgumentNullException.ThrowIfNull(proxyUri);
        if (!proxyUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A workspace network proxy must use an absolute URI.",
                nameof(proxyUri));
        }

        return new ProxyRoute(proxyUri);
    }

    /// <summary>
    /// A route attached at the workspace boundary, such as a VPN or tailnet.
    /// Its concrete adapter owns setup and cleanup; clients use the routed
    /// runtime services without needing provider-specific configuration.
    /// </summary>
    public static WorkspaceNetworkRoute Attached { get; } = new AttachedRoute();

    private sealed record DirectRoute : WorkspaceNetworkRoute;

    private sealed record ProxyRoute(Uri Address) : WorkspaceNetworkRoute
    {
        public override Uri ProxyUri => Address;
    }

    private sealed record AttachedRoute : WorkspaceNetworkRoute;
}

public sealed class WorkspaceRuntimeServices(
    WorkspaceRuntimeBackends backends,
    WorkspaceNetworkRoute networkRoute,
    IAsyncDisposable? lifetime = null) : IAsyncDisposable
{
    public WorkspaceRuntimeBackends Backends { get; } = backends
        ?? throw new ArgumentNullException(nameof(backends));

    public WorkspaceNetworkRoute NetworkRoute { get; } = networkRoute
        ?? throw new ArgumentNullException(nameof(networkRoute));

    public ValueTask DisposeAsync() =>
        lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed record WorkspaceRuntimeServicesRequest
{
    public WorkspaceRuntimeServicesRequest(
        WorkspaceInstanceId workspaceId,
        IConnectionRuntime connectionRuntime,
        WorkspaceRuntimeServices hostServices,
        WorkspaceIsolationBinding? isolationBinding)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "A workspace runtime services request requires a workspace ID.",
                nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        ConnectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        HostServices = hostServices
            ?? throw new ArgumentNullException(nameof(hostServices));
        IsolationBinding = isolationBinding;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public IConnectionRuntime ConnectionRuntime { get; }

    public WorkspaceRuntimeServices HostServices { get; }

    public WorkspaceIsolationBinding? IsolationBinding { get; }
}

public interface IWorkspaceRuntimeServicesFactory
{
    WorkspaceRuntimeServices Create(WorkspaceRuntimeServicesRequest request);
}
