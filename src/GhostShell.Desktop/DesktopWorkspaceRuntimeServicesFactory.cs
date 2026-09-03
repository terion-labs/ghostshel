using GhostShell.App;
using GhostShell.Application;
using GhostShell.Databases;
using GhostShell.Docker;
using GhostShell.Git;
using GhostShell.Infrastructure;
using GhostShell.Monitoring;
using GhostShell.Redis;

namespace GhostShell.Desktop;

internal sealed class DesktopWorkspaceRuntimeServicesFactory(
    IConnectionExecutableLocator executableLocator,
    TimeProvider timeProvider,
    IBrowserRendererViewFactory browserRendererViewFactory,
    WorkspaceSystemMonitorPanelSessionFactory systemMonitorFactory) : IWorkspaceRuntimeServicesFactory
{
    public WorkspaceRuntimeServices Create(WorkspaceRuntimeServicesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsolationBinding is not { } binding)
        {
            return request.HostServices;
        }

        var executor = new ConnectionCommandExecutor(
            request.ConnectionRuntime,
            executableLocator);
        if (request.ConnectionRuntime is not IConnectionCommandRuntime commandRuntime)
        {
            throw new InvalidOperationException(
                "The workspace isolation runtime cannot plan panel commands.");
        }

        var tunnelFactory = new WorkspaceIsolationTcpTunnelFactory(commandRuntime);
        var databases = new DatabasePanelClient(tunnelFactory);
        var socksProxy = new WorkspaceIsolationSocksProxy(
            commandRuntime,
            GhostShell.Core.BuiltInConnections.Local);
        var browserFactory = new IsolatedBrowserRendererViewFactory(
            browserRendererViewFactory,
            commandRuntime,
            socksProxy,
            binding.ResourceName);
        var monitors = new SystemMonitorPanelSessionFactory(executor, timeProvider);
        var monitorRegistration = systemMonitorFactory.Register(
            request.WorkspaceId,
            monitors);
        var lifetime = new IsolatedWorkspaceRuntimeLifetime(
            databases,
            socksProxy,
            browserFactory,
            monitorRegistration,
            monitors);
        return new WorkspaceRuntimeServices(
            new WorkspaceRuntimeBackends(
                new DockerEngineClient(executor, timeProvider),
                new GitRepositoryClient(executor, timeProvider),
                new IsolatedPosixFilePanelClient(executor),
                fileTransferQueueClient: null,
                databases,
                new RedisPanelSessionFactory(tunnelFactory),
                browserFactory),
            WorkspaceNetworkRoute.ViaProxy(new Uri(
                $"socks5://127.0.0.1:{socksProxy.LocalPort}",
                UriKind.Absolute)),
            lifetime);
    }

    private sealed class IsolatedWorkspaceRuntimeLifetime(
        DatabasePanelClient databasePanelClient,
        WorkspaceIsolationSocksProxy socksProxy,
        IsolatedBrowserRendererViewFactory browserRendererViewFactory,
        IDisposable monitorRegistration,
        SystemMonitorPanelSessionFactory monitorFactory) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private bool _browserDisposed;
        private bool _databaseDisposed;
        private bool _monitorFactoryDisposed;
        private bool _monitorRegistrationDisposed;
        private bool _socksDisposed;

        public async ValueTask DisposeAsync()
        {
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Exception> errors = [];
                await TryDisposeAsync(
                    _databaseDisposed,
                    databasePanelClient.DisposeAsync,
                    () => _databaseDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _browserDisposed,
                    browserRendererViewFactory.DisposeAsync,
                    () => _browserDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _socksDisposed,
                    socksProxy.DisposeAsync,
                    () => _socksDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _monitorRegistrationDisposed,
                    () => DisposeAsync(monitorRegistration),
                    () => _monitorRegistrationDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _monitorFactoryDisposed,
                    () => DisposeAsync(monitorFactory),
                    () => _monitorFactoryDisposed = true,
                    errors).ConfigureAwait(false);
                if (errors.Count > 0)
                {
                    throw new AggregateException(
                        "One or more workspace runtime services could not be disposed.",
                        errors);
                }
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        private static ValueTask DisposeAsync(IDisposable disposable)
        {
            disposable.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async ValueTask TryDisposeAsync(
            bool isDisposed,
            Func<ValueTask> disposeAsync,
            Action markDisposed,
            ICollection<Exception> errors)
        {
            if (isDisposed)
            {
                return;
            }

            try
            {
                await disposeAsync().ConfigureAwait(false);
                markDisposed();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
    }
}
