using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

public interface IWorkspaceIsolationEgressGuard
{
    ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        NetworkConnectionProfile connection,
        CancellationToken cancellationToken);

    ValueTask<NetworkConnectionResult<Unit>> DisarmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken);
}

public sealed record WorkspaceIsolationEgressGuardArmResult
{
    private WorkspaceIsolationEgressGuardArmResult(
        bool isEnforced,
        NetworkConnectionError? error)
    {
        if (!isEnforced && error is null)
        {
            throw new ArgumentException(
                "A successful isolation egress guard must be enforced.",
                nameof(isEnforced));
        }

        IsEnforced = isEnforced;
        Error = error;
    }

    public bool IsEnforced { get; }

    public NetworkConnectionError? Error { get; }

    public static WorkspaceIsolationEgressGuardArmResult Enforced() => new(true, null);

    public static WorkspaceIsolationEgressGuardArmResult Failed(
        NetworkConnectionError error,
        bool isEnforced) =>
        new(isEnforced, error ?? throw new ArgumentNullException(nameof(error)));
}

internal static class WorkspaceIsolationNetworkNames
{
    public const string Namespace = "ghostshell-vpn";

    public static string TunnelInterface(NetworkConnectionId connectionId)
    {
        var source = Encoding.UTF8.GetBytes(connectionId.Value);
        try
        {
            return $"gs{Convert.ToHexString(SHA256.HashData(source))[..10].ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }
}

/// <summary>
/// Moves all locally-originated isolate traffic through a private Linux network namespace.
/// Before its VPN interface is ready, that namespace drops forwarded traffic while still
/// allowing the VPN process itself to reach its control endpoint through the isolate uplink.
/// </summary>
public sealed class WorkspaceIsolationEgressGuard : IWorkspaceIsolationEgressGuard
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private const string RootFunction =
        "as_root() { if [ \"$(id -u)\" -eq 0 ]; then \"$@\"; else sudo -n \"$@\"; fi; }; ";

    private const string ArmScript = RootFunction + """
        set -eu
        tunnel=$1
        namespace=ghostshell-vpn
        state=/run/ghostshell-network-guard
        main_veth=gs-main
        vpn_veth=gs-vpn
        guard_installed=0
        preserve_guard_status() {
            status=$?
            trap - EXIT
            if [ "$status" -ne 0 ] && [ "$guard_installed" -eq 1 ]; then exit 78; fi
            exit "$status"
        }
        trap preserve_guard_status EXIT
        for tool in ip nft sysctl; do
            command -v "$tool" >/dev/null 2>&1 || exit 69
        done
        if [ "$(id -u)" -ne 0 ]; then
            command -v sudo >/dev/null 2>&1 || exit 77
            sudo -n true >/dev/null 2>&1 || exit 77
        fi
        as_root sh -c '
            umask 077
            mkdir -p "$1"
            if [ ! -f "$1/ip-forward" ]; then sysctl -n net.ipv4.ip_forward > "$1/ip-forward"; fi
            if [ ! -f "$1/ip6-forward" ]; then sysctl -n net.ipv6.conf.all.forwarding > "$1/ip6-forward"; fi
        ' ghostshell-guard "$state"
        guard_rules='
            table inet ghostshell_guard {
                chain mark_output {
                    type route hook output priority mangle; policy accept;
                    oifname "lo" return
                    meta mark set 0x4753
                }
                chain block_output {
                    type filter hook output priority filter; policy drop;
                    oifname "lo" accept
                    oifname "gs-main" accept
                }
            }
        '
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then
            {
                printf '%s\n' 'delete table inet ghostshell_guard'
                printf '%s\n' "$guard_rules"
            } | as_root nft -f -
        else
            printf '%s\n' "$guard_rules" | as_root nft -f -
        fi
        as_root nft list chain inet ghostshell_guard mark_output >/dev/null
        as_root nft list chain inet ghostshell_guard block_output \
            | grep -Eq 'policy drop'
        guard_installed=1
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null || true); do
                as_root kill "$process" >/dev/null 2>&1 || true
            done
            sleep 1
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null || true); do
                as_root kill -9 "$process" >/dev/null 2>&1 || true
            done
            as_root ip netns delete "$namespace" >/dev/null 2>&1 || true
        fi
        as_root ip link delete "$main_veth" >/dev/null 2>&1 || true
        as_root ip netns add "$namespace"
        as_root ip link add "$main_veth" type veth peer name "$vpn_veth"
        as_root ip link set "$vpn_veth" netns "$namespace"
        as_root ip address add 169.254.254.1/30 dev "$main_veth"
        as_root ip -6 address add fd42:4753::1/64 dev "$main_veth"
        as_root ip link set "$main_veth" up
        as_root ip netns exec "$namespace" ip link set lo up
        as_root ip netns exec "$namespace" ip address add 169.254.254.2/30 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip -6 address add fd42:4753::2/64 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip link set "$vpn_veth" up
        as_root ip netns exec "$namespace" ip route replace default via 169.254.254.1 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip -6 route replace default via fd42:4753::1 dev "$vpn_veth"
        as_root sysctl -q -w net.ipv4.ip_forward=1
        as_root sysctl -q -w net.ipv6.conf.all.forwarding=1
        as_root ip netns exec "$namespace" sysctl -q -w net.ipv4.ip_forward=1
        as_root ip netns exec "$namespace" sysctl -q -w net.ipv6.conf.all.forwarding=1
        as_root ip rule delete priority 100 fwmark 0x4753 table 4242 >/dev/null 2>&1 || true
        as_root ip -6 rule delete priority 100 fwmark 0x4753 table 4242 >/dev/null 2>&1 || true
        as_root ip route replace table 4242 default via 169.254.254.2 dev "$main_veth"
        as_root ip -6 route replace table 4242 default via fd42:4753::2 dev "$main_veth"
        as_root ip rule add priority 100 fwmark 0x4753 table 4242
        as_root ip -6 rule add priority 100 fwmark 0x4753 table 4242
        as_root nft delete table ip ghostshell_vpn_nat >/dev/null 2>&1 || true
        as_root nft delete table ip6 ghostshell_vpn_nat >/dev/null 2>&1 || true
        printf '%s\n' '
            table ip ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "gs-main" masquerade
                    ip saddr 169.254.254.0/30 oifname != "gs-main" masquerade
                }
            }
        ' | as_root nft -f -
        printf '%s\n' '
            table ip6 ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "gs-main" masquerade
                    ip6 saddr fd42:4753::/64 oifname != "gs-main" masquerade
                }
            }
        ' | as_root nft -f -
        printf '
            table inet ghostshell_guard {
                chain forward {
                    type filter hook forward priority filter; policy drop;
                    iifname "gs-vpn" oifname "%s" accept
                    iifname "%s" oifname "gs-vpn" ct state established,related accept
                }
            }
            table ip ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "%s" masquerade
                }
            }
            table ip6 ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "%s" masquerade
                }
            }
        ' "$tunnel" "$tunnel" "$tunnel" "$tunnel" \
            | as_root ip netns exec "$namespace" nft -f -
        trap - EXIT
        exit 0
        """;

    private const string DisarmScript = RootFunction + """
        set +e
        namespace=ghostshell-vpn
        state=/run/ghostshell-network-guard
        cleanup_failed=0
        try_cleanup() {
            "$@" >/dev/null 2>&1 || cleanup_failed=1
        }
        restore_sysctl() {
            key=$1
            file=$2
            value=$(as_root cat "$file" 2>/dev/null)
            if [ -z "$value" ]; then
                cleanup_failed=1
                return
            fi
            try_cleanup as_root sysctl -q -w "$key=$value"
            actual=$(as_root sysctl -n "$key" 2>/dev/null)
            if [ "$actual" != "$value" ]; then cleanup_failed=1; fi
        }
        for tool in ip nft sysctl; do
            command -v "$tool" >/dev/null 2>&1 || exit 69
        done
        if [ "$(id -u)" -ne 0 ]; then
            command -v sudo >/dev/null 2>&1 || exit 77
            sudo -n true >/dev/null 2>&1 || exit 77
        fi
        guard_present=0
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then
            guard_present=1
        fi
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null); do
                as_root kill "$process" >/dev/null 2>&1
            done
            sleep 1
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null); do
                as_root kill -9 "$process" >/dev/null 2>&1
            done
            as_root ip netns delete "$namespace" >/dev/null 2>&1
        fi
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            cleanup_failed=1
        fi
        if as_root ip link show gs-main >/dev/null 2>&1; then
            try_cleanup as_root ip link delete gs-main
        fi
        if as_root ip link show gs-main >/dev/null 2>&1; then cleanup_failed=1; fi
        while as_root ip rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        while as_root ip -6 rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        if as_root ip rule show | grep -Eq 'fwmark 0x4753.*lookup 4242'; then
            cleanup_failed=1
        fi
        if as_root ip -6 rule show | grep -Eq 'fwmark 0x4753.*lookup 4242'; then
            cleanup_failed=1
        fi
        try_cleanup as_root ip route flush table 4242
        try_cleanup as_root ip -6 route flush table 4242
        if as_root nft list table ip ghostshell_vpn_nat >/dev/null 2>&1; then
            try_cleanup as_root nft delete table ip ghostshell_vpn_nat
        fi
        if as_root nft list table ip6 ghostshell_vpn_nat >/dev/null 2>&1; then
            try_cleanup as_root nft delete table ip6 ghostshell_vpn_nat
        fi
        if as_root nft list table ip ghostshell_vpn_nat >/dev/null 2>&1; then
            cleanup_failed=1
        fi
        if as_root nft list table ip6 ghostshell_vpn_nat >/dev/null 2>&1; then
            cleanup_failed=1
        fi
        if as_root test -d "$state"; then
            restore_sysctl net.ipv4.ip_forward "$state/ip-forward"
            restore_sysctl net.ipv6.conf.all.forwarding "$state/ip6-forward"
        elif [ "$guard_present" -eq 1 ]; then
            cleanup_failed=1
        fi
        if [ "$cleanup_failed" -ne 0 ]; then exit 70; fi
        try_cleanup as_root rm -rf -- "$state"
        if as_root test -d "$state"; then cleanup_failed=1; fi
        if [ "$cleanup_failed" -ne 0 ]; then exit 70; fi
        as_root nft delete table inet ghostshell_guard >/dev/null 2>&1
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then exit 70; fi
        exit 0
        """;

    private readonly IWorkspaceIsolationProvider _isolationProvider;
    private readonly IWorkspaceIsolationCommandRunner _commandRunner;

    public WorkspaceIsolationEgressGuard(IWorkspaceIsolationProvider isolationProvider)
        : this(isolationProvider, new WorkspaceIsolationCommandRunner())
    {
    }

    internal WorkspaceIsolationEgressGuard(
        IWorkspaceIsolationProvider isolationProvider,
        IWorkspaceIsolationCommandRunner commandRunner)
    {
        _isolationProvider = isolationProvider
            ?? throw new ArgumentNullException(nameof(isolationProvider));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    public ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        NetworkConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        Validate(workspaceId, binding);
        ArgumentNullException.ThrowIfNull(connection);
        return RunArmAsync(
            binding,
            ArmScript,
            [WorkspaceIsolationNetworkNames.TunnelInterface(connection.Id)],
            "workspace_network_kill_switch_arm_failed",
            "The workspace network kill switch could not be enabled inside the isolate.",
            cancellationToken);
    }

    public ValueTask<NetworkConnectionResult<Unit>> DisarmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        Validate(workspaceId, binding);
        return RunAsync(
            binding,
            DisarmScript,
            [],
            "workspace_network_kill_switch_disarm_failed",
            "The workspace network kill switch could not restore direct isolate egress.",
            cancellationToken);
    }

    private async ValueTask<NetworkConnectionResult<Unit>> RunAsync(
        WorkspaceIsolationBinding binding,
        string script,
        IReadOnlyList<string> arguments,
        string stableCode,
        string message,
        CancellationToken cancellationToken)
    {
        var launch = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", script, "ghostshell-network-guard", .. arguments]));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure launchFailure)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.RouteUnavailable,
                stableCode,
                launchFailure.Error.Message,
                launchFailure.Error.Retryable));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            var result = await _commandRunner.RunAsync(
                    ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value,
                    ReadOnlyMemory<byte>.Empty,
                    deadline.Token)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return NetworkConnectionResult<Unit>.Succeed(Unit.Value);
            }

            var runtimeMissing = result.ExitCode == 69;
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                runtimeMissing
                    ? NetworkConnectionErrorCode.RuntimeMissing
                    : NetworkConnectionErrorCode.RouteUnavailable,
                runtimeMissing ? "workspace_network_guard_runtime_missing" : stableCode,
                runtimeMissing
                    ? "The isolated workspace needs iproute2, nftables, and procps to enforce its network kill switch."
                    : message,
                retryable: !runtimeMissing));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "workspace_network_kill_switch_cancelled",
                "Changing the workspace network kill switch was cancelled.",
                retryable: false));
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.RouteUnavailable,
                stableCode,
                message,
                retryable: true));
        }
    }

    private async ValueTask<WorkspaceIsolationEgressGuardArmResult> RunArmAsync(
        WorkspaceIsolationBinding binding,
        string script,
        IReadOnlyList<string> arguments,
        string stableCode,
        string message,
        CancellationToken cancellationToken)
    {
        var launch = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", script, "ghostshell-network-guard", .. arguments]));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure launchFailure)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    stableCode,
                    launchFailure.Error.Message,
                    launchFailure.Error.Retryable),
                isEnforced: false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            var result = await _commandRunner.RunAsync(
                    ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value,
                    ReadOnlyMemory<byte>.Empty,
                    deadline.Token)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return WorkspaceIsolationEgressGuardArmResult.Enforced();
            }

            var runtimeMissing = result.ExitCode == 69;
            var privilegesMissing = result.ExitCode == 77;
            var error = new NetworkConnectionError(
                runtimeMissing
                    ? NetworkConnectionErrorCode.RuntimeMissing
                    : NetworkConnectionErrorCode.RouteUnavailable,
                runtimeMissing
                    ? "workspace_network_guard_runtime_missing"
                    : privilegesMissing
                        ? "workspace_network_guard_privileges_missing"
                        : stableCode,
                runtimeMissing
                    ? "The isolated workspace needs iproute2, nftables, and procps to enforce its network kill switch."
                    : privilegesMissing
                        ? "The workspace user cannot administer the isolated network namespace required by the kill switch."
                        : message,
                retryable: !runtimeMissing && !privilegesMissing);
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                error,
                isEnforced: result.ExitCode == 78);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.Cancelled,
                    "workspace_network_kill_switch_cancelled",
                    "Changing the workspace network kill switch was cancelled.",
                    retryable: false),
                isEnforced: false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    stableCode,
                    message,
                    retryable: true),
                isEnforced: false);
        }
    }

    private static void Validate(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace instance ID is required.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(binding);
        const WorkspaceIsolationCapability required =
            WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.StructuredProcessExecution;
        if ((binding.Capabilities & required) != required)
        {
            throw new ArgumentException(
                "The workspace binding cannot enforce isolated network egress.",
                nameof(binding));
        }
    }
}
