using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

internal sealed class WorkspaceNetworkConnectionRuntime(
    IConnectionRuntime inner,
    WorkspaceNetworkEgressState egressState,
    bool injectProxyEnvironment) : IConnectionRuntime
{
    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await inner.PlanOpenAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        return Apply(profile, result);
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await inner.PlanOpenAsync(
                profile,
                multiplexerSession,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return Apply(profile, result);
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var egress = egressState.Current;
        if (egress == WorkspaceNetworkEgress.Direct)
        {
            return inner.TestAsync(profile, progress, cancellationToken);
        }

        _ = progress;
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(
            egress == WorkspaceNetworkEgress.Blocked
                ? new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.Offline,
                    "workspace_network_kill_switch_blocked",
                    "The workspace network kill switch is blocking traffic.",
                    Retryable: true,
                    ConnectionRecoveryAction.Reconnect)
                : new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.UnsupportedPlatform,
                    "workspace_network_route_test_unavailable",
                    "Connection tests are unavailable until they can use the active workspace network route.",
                    Retryable: false,
                    ConnectionRecoveryAction.None)));
    }

    private ConnectionRuntimeResult<ConnectionOpenPlan> Apply(
        ConnectionProfile profile,
        ConnectionRuntimeResult<ConnectionOpenPlan> result)
    {
        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure)
        {
            return result;
        }

        var egress = egressState.Current;
        if (egress == WorkspaceNetworkEgress.Blocked)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.Offline,
                    "workspace_network_kill_switch_blocked",
                    "The workspace network kill switch is blocking traffic.",
                    Retryable: true,
                    ConnectionRecoveryAction.Reconnect));
        }

        if (!injectProxyEnvironment)
        {
            return result;
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
        var launch = plan.Launch;
        var proxy = egressState.LocalProxyEndpoint ?? egress.ProxyEndpoint;
        if (proxy is null)
        {
            return result;
        }

        var environment = new Dictionary<string, string>(launch.Environment, StringComparer.Ordinal)
        {
            ["ALL_PROXY"] = proxy.AbsoluteUri,
            ["HTTPS_PROXY"] = proxy.AbsoluteUri,
            ["HTTP_PROXY"] = proxy.AbsoluteUri,
            ["all_proxy"] = proxy.AbsoluteUri,
            ["https_proxy"] = proxy.AbsoluteUri,
            ["http_proxy"] = proxy.AbsoluteUri,
        };
        var arguments = launch.Arguments;
        if (profile.Endpoint is ConnectionEndpoint.Ssh ssh
            && WorkspaceSshProxyCommand.TryCreate(proxy, ssh, out var proxyCommand))
        {
            arguments = ["-o", $"ProxyCommand={proxyCommand}", .. arguments];
            environment["GIT_SSH_COMMAND"] = $"ssh -o ProxyCommand={proxyCommand}";
            environment["DOCKER_SSH_COMMAND"] = $"ssh -o ProxyCommand={proxyCommand}";
        }
        var routedLaunch = new TerminalLaunchRequest(
            launch.WorkingDirectory,
            launch.Executable,
            arguments,
            environment,
            launch.RenderProfile,
            launch.Keymap,
            launch.ConnectionId,
            launch.ConnectionMetadata,
            launch.InitialCommand,
            launch.ShellActivityFallback,
            launch.MultiplexerSession);
        return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
            new ConnectionOpenPlan(
                plan.ConnectionId,
                plan.Kind,
                routedLaunch,
                plan.Authentication,
                plan.HostKeyPolicy,
                plan.ReconnectMode,
                plan.SecretRequirements,
                plan.Warnings,
                plan.IsSecretBrokerPrepared));
    }

    private static class WorkspaceSshProxyCommand
    {
        public const string Switch = "--ghostshell-workspace-socks-connect";

        public static bool TryCreate(
            Uri proxy,
            ConnectionEndpoint.Ssh endpoint,
            out string command)
        {
            command = string.Empty;
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            var encodedHost = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(endpoint.Host));
            command = $"\"{processPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\" "
                + $"{Switch} {proxy.Port} {encodedHost} {endpoint.Port}";
            return true;
        }
    }
}
