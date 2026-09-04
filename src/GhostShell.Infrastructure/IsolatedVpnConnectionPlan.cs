using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal sealed record IsolatedVpnSecretFile(string FileName, byte[] Content);

internal sealed record IsolatedVpnPreflight(
    string DisplayName,
    string InstallHint,
    string Script);

internal sealed record IsolatedVpnConnectionPlan(
    string DisplayName,
    string InstallHint,
    string PreflightScript,
    string AttachScript,
    string HealthScript,
    string CleanupScript,
    IReadOnlyList<string> AttachArguments,
    IReadOnlyList<string> HealthArguments,
    IReadOnlyList<string> CleanupArguments,
    IReadOnlyList<IsolatedVpnSecretFile> SecretFiles,
    byte[]? StandardInput);

internal static class IsolatedVpnConnectionPlans
{
    private const string RootFunction =
        "as_root() { if [ \"$(id -u)\" -eq 0 ]; then \"$@\"; else sudo -n \"$@\"; fi; }; "
        + "in_vpn() { if as_root ip netns list 2>/dev/null | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; "
        + "then as_root ip netns exec ghostshell-vpn \"$@\"; else as_root \"$@\"; fi; }; "
        + "route_uses_iface() { family=$1; prefix=$2; iface=$3; "
        + "in_vpn ip \"-$family\" route show table all \"$prefix\" 2>/dev/null "
        + "| awk -v iface=\"$iface\" '{ for (i = 1; i < NF; i++) "
        + "if ($i == \"dev\" && $(i + 1) == iface) found = 1 } END { exit found ? 0 : 1 }'; }; "
        + "has_non_tunnel_default() { family=$1; iface=$2; "
        + "in_vpn ip \"-$family\" route show table all default 2>/dev/null "
        + "| awk -v iface=\"$iface\" '{ tunnel = 0; for (i = 1; i < NF; i++) "
        + "if ($i == \"dev\" && $(i + 1) == iface) tunnel = 1; if (!tunnel) found = 1 } "
        + "END { exit found ? 0 : 1 }'; }; "
        + "require_full_route() { iface=$1; "
        + "route_uses_iface 4 default \"$iface\" "
        + "|| { route_uses_iface 4 0.0.0.0/1 \"$iface\" "
        + "&& route_uses_iface 4 128.0.0.0/1 \"$iface\"; } || return 64; "
        + "if has_non_tunnel_default 6 \"$iface\"; then "
        + "route_uses_iface 6 default \"$iface\" "
        + "|| { route_uses_iface 6 ::/1 \"$iface\" "
        + "&& route_uses_iface 6 8000::/1 \"$iface\"; } || return 64; fi; }; "
        + "process_is_expected() { pidfile=$1; expected=$2; [ -s \"$pidfile\" ] || return 1; "
        + "process=$(as_root cat \"$pidfile\" 2>/dev/null) || return 1; "
        + "case \"$process\" in ''|*[!0-9]*) return 1;; esac; "
        + "as_root kill -0 \"$process\" >/dev/null 2>&1 || return 1; "
        + "actual=$(as_root cat \"/proc/$process/comm\" 2>/dev/null) || return 1; "
        + "[ \"$actual\" = \"$expected\" ]; }; "
        + "stop_expected_process() { pidfile=$1; expected=$2; [ -e \"$pidfile\" ] || return 0; "
        + "process=$(as_root cat \"$pidfile\" 2>/dev/null) || return 1; "
        + "case \"$process\" in ''|*[!0-9]*) return 1;; esac; "
        + "as_root kill -0 \"$process\" >/dev/null 2>&1 || return 0; "
        + "actual=$(as_root cat \"/proc/$process/comm\" 2>/dev/null) || return 1; "
        + "[ \"$actual\" = \"$expected\" ] || return 1; "
        + "as_root kill \"$process\" >/dev/null 2>&1 || return 1; remaining=10; "
        + "while as_root kill -0 \"$process\" >/dev/null 2>&1 && [ \"$remaining\" -gt 0 ]; "
        + "do sleep 1; remaining=$((remaining - 1)); done; "
        + "if as_root kill -0 \"$process\" >/dev/null 2>&1; then "
        + "as_root kill -9 \"$process\" >/dev/null 2>&1 || return 1; sleep 1; fi; "
        + "! as_root kill -0 \"$process\" >/dev/null 2>&1; }; "
        + "remove_interface() { iface=$1; if in_vpn ip link show dev \"$iface\" >/dev/null 2>&1; "
        + "then in_vpn ip link delete \"$iface\" >/dev/null 2>&1 || return 1; fi; "
        + "! in_vpn ip link show dev \"$iface\" >/dev/null 2>&1; }; "
        + "probe_routed_reachability() { iface=$1; "
        + "for url in https://1.1.1.1/cdn-cgi/trace https://1.0.0.1/cdn-cgi/trace; do "
        + "in_vpn curl --interface \"$iface\" --noproxy '*' --proto '=https' --fail --silent "
        + "--show-error --connect-timeout 3 --max-time 6 --output /dev/null \"$url\" "
        + "&& return 0; done; return 65; }; ";

    private const string PrivilegePreflight =
        "if [ \"$(id -u)\" -ne 0 ]; then command -v sudo >/dev/null 2>&1 || exit 77; "
        + "sudo -n true >/dev/null 2>&1 || exit 77; fi; "
        + "command -v curl >/dev/null 2>&1 || exit 68; ";

    private const string WireGuardPreflight =
        PrivilegePreflight
        + "command -v ip >/dev/null 2>&1 && command -v wg >/dev/null 2>&1 "
        + "&& command -v wg-quick >/dev/null 2>&1 || exit 69";

    private const string OpenVpnPreflight =
        PrivilegePreflight
        + "command -v ip >/dev/null 2>&1 && command -v openvpn >/dev/null 2>&1 || exit 69";

    private const string AnyConnectPreflight =
        PrivilegePreflight
        + "command -v openconnect >/dev/null 2>&1 && command -v ip >/dev/null 2>&1 || exit 69";

    private const string TailscalePreflight =
        PrivilegePreflight
        + "command -v ip >/dev/null 2>&1 && command -v tailscale >/dev/null 2>&1 "
        + "&& command -v tailscaled >/dev/null 2>&1 || exit 69";

    private const string WireGuardAttach = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        config="$dir/$iface.conf"
        in_vpn wg-quick up "$config" || exit 70
        if ! in_vpn wg show "$iface" >/dev/null 2>&1; then
            in_vpn wg-quick down "$config" >/dev/null 2>&1 || true
            exit 70
        fi
        require_full_route "$iface"
        """;

    private const string WireGuardHealth = RootFunction + """
        set -eu
        iface=$1
        in_vpn wg show "$iface" >/dev/null 2>&1 || exit 70
        require_full_route "$iface"
        probe_routed_reachability "$iface"
        """;

    private const string WireGuardCleanup = RootFunction + """
        set -u
        dir=$1
        iface=$2
        [ -d "$dir" ] || exit 0
        config="$dir/$iface.conf"
        if command -v wg-quick >/dev/null 2>&1 && [ -f "$config" ]; then
            in_vpn wg-quick down "$config" >/dev/null 2>&1 || true
        fi
        remove_interface "$iface" || exit 70
        as_root rm -rf -- "$dir"
        """;

    private const string OpenVpnAttach = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        config="$dir/openvpn.conf"
        pid="$dir/openvpn.pid"
        log="$dir/openvpn.log"
        in_vpn openvpn --config "$config" --dev "$iface" --daemon \
            --writepid "$pid" --log "$log" || exit 70
        remaining=90
        while [ "$remaining" -gt 0 ]; do
            if grep -q 'Initialization Sequence Completed' "$log" 2>/dev/null; then
                in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
                require_full_route "$iface"
                exit 0
            fi
            if [ -s "$pid" ]; then
                process=$(as_root cat "$pid")
                case "$process" in ''|*[!0-9]*) exit 70;; esac
                as_root kill -0 "$process" >/dev/null 2>&1 || break
            fi
            sleep 1
            remaining=$((remaining - 1))
        done
        if grep -Eqi 'AUTH_FAILED|authentication failed|username/password' "$log" 2>/dev/null; then
            exit 77
        fi
        if grep -Eqi 'Options error|Cannot load|Error opening configuration' "$log" 2>/dev/null; then
            exit 64
        fi
        exit 70
        """;

    private const string OpenVpnHealth = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        process_is_expected "$dir/openvpn.pid" openvpn || exit 70
        in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
        require_full_route "$iface"
        probe_routed_reachability "$iface"
        """;

    private const string OpenVpnCleanup = RootFunction + """
        set -u
        dir=$1
        iface=$2
        [ -d "$dir" ] || exit 0
        pid="$dir/openvpn.pid"
        stop_expected_process "$pid" openvpn || exit 70
        remove_interface "$iface" || exit 70
        as_root rm -rf -- "$dir"
        """;

    private const string AnyConnectAttach = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        shift 2
        umask 077
        mkdir -p -- "$dir"
        pid="$dir/openconnect.pid"
        in_vpn openconnect --background --pid-file="$pid" --interface="$iface" \
            --non-inter "$@" || exit $?
        in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
        process_is_expected "$pid" openconnect || exit 70
        require_full_route "$iface"
        """;

    private const string AnyConnectHealth = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        process_is_expected "$dir/openconnect.pid" openconnect || exit 70
        in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
        require_full_route "$iface"
        probe_routed_reachability "$iface"
        """;

    private const string AnyConnectCleanup = RootFunction + """
        set -u
        dir=$1
        iface=$2
        [ -d "$dir" ] || exit 0
        pid="$dir/openconnect.pid"
        stop_expected_process "$pid" openconnect || exit 70
        remove_interface "$iface" || exit 70
        as_root rm -rf -- "$dir"
        """;

    private const string TailscaleAttach = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        exit_node=$3
        control_server=$4
        auth_file=$5
        umask 077
        mkdir -p -- "$dir"
        socket=/run/tailscale/tailscaled.sock
        mode=system-existing
        if as_root ip netns list 2>/dev/null | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            mode=private
            socket="$dir/tailscaled.sock"
            state="$dir/tailscaled.state"
            log="$dir/tailscaled.log"
            pid="$dir/tailscaled.pid"
            in_vpn sh -c 'umask 077; tailscaled --state="$1" --socket="$2" --tun="$3" >"$5" 2>&1 & echo $! >"$4"' \
                ghostshell-tailscale "$state" "$socket" "$iface" "$pid" "$log"
        elif command -v systemctl >/dev/null 2>&1 \
            && as_root systemctl show-environment >/dev/null 2>&1; then
            if ! as_root systemctl is-active --quiet tailscaled; then
                as_root systemctl start tailscaled || exit 70
                mode=system-started
            fi
        else
            mode=private
            socket="$dir/tailscaled.sock"
            state="$dir/tailscaled.state"
            log="$dir/tailscaled.log"
            pid="$dir/tailscaled.pid"
            as_root sh -c 'umask 077; tailscaled --state="$1" --socket="$2" --tun="$3" >"$5" 2>&1 & echo $! >"$4"' \
                ghostshell-tailscale "$state" "$socket" "$iface" "$pid" "$log"
        fi
        printf '%s\n' "$mode" > "$dir/tailscale.mode"
        printf '%s\n' "$socket" > "$dir/tailscale.socket"
        remaining=30
        while [ "$remaining" -gt 0 ]; do
            as_root test -S "$socket" && break
            sleep 1
            remaining=$((remaining - 1))
        done
        as_root test -S "$socket" || exit 70
        set -- --socket="$socket" up --exit-node="$exit_node" \
            --exit-node-allow-lan-access=false --accept-routes=true
        if [ -n "$control_server" ]; then
            set -- "$@" --login-server="$control_server"
        fi
        if [ -n "$auth_file" ]; then
            set -- "$@" --auth-key="file:$auth_file"
        fi
        output=$(in_vpn tailscale "$@" 2>&1) || {
            printf '%s\n' "$output" >&2
            case "$output" in *[Aa]uthentication*|*[Ll]ogin*|*authkey*) exit 77;; esac
            exit 70
        }
        status=$(in_vpn tailscale --socket="$socket" status --json 2>/dev/null || true)
        if printf '%s\n' "$status" | grep -Eq '"BackendState"[[:space:]]*:[[:space:]]*"Running"'; then
            in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
            require_full_route "$iface"
            exit 0
        fi
        if printf '%s\n' "$status" | grep -Eq '"BackendState"[[:space:]]*:[[:space:]]*"NeedsLogin"'; then
            exit 77
        fi
        exit 70
        """;

    private const string TailscaleHealth = RootFunction + """
        set -eu
        dir=$1
        iface=$2
        socket=$(cat "$dir/tailscale.socket" 2>/dev/null) || exit 70
        mode=$(cat "$dir/tailscale.mode" 2>/dev/null) || exit 70
        if [ "$mode" = private ]; then
            process_is_expected "$dir/tailscaled.pid" tailscaled || exit 70
        elif [ "$mode" = system-started ]; then
            as_root systemctl is-active --quiet tailscaled || exit 70
        fi
        status=$(in_vpn tailscale --socket="$socket" status --json 2>/dev/null) || exit 70
        printf '%s\n' "$status" \
            | grep -Eq '"BackendState"[[:space:]]*:[[:space:]]*"Running"' || exit 70
        in_vpn ip link show dev "$iface" >/dev/null 2>&1 || exit 70
        require_full_route "$iface"
        probe_routed_reachability "$iface"
        """;

    private const string TailscaleCleanup = RootFunction + """
        set -u
        dir=$1
        iface=$2
        [ -d "$dir" ] || exit 0
        socket=$(cat "$dir/tailscale.socket" 2>/dev/null || true)
        mode=$(cat "$dir/tailscale.mode" 2>/dev/null || true)
        if [ -n "$socket" ] && command -v tailscale >/dev/null 2>&1; then
            in_vpn tailscale --socket="$socket" down >/dev/null 2>&1 || true
        fi
        if [ "$mode" = system-started ]; then
            command -v systemctl >/dev/null 2>&1 || exit 70
            as_root systemctl stop tailscaled >/dev/null 2>&1 || exit 70
            ! as_root systemctl is-active --quiet tailscaled || exit 70
        elif [ "$mode" = private ]; then
            [ -s "$dir/tailscaled.pid" ] || exit 70
            stop_expected_process "$dir/tailscaled.pid" tailscaled || exit 70
        fi
        remove_interface "$iface" || exit 70
        as_root rm -rf -- "$dir"
        """;

    public static IsolatedVpnPreflight Preflight(NetworkConnectionKind kind) => kind switch
    {
        NetworkConnectionKind.WireGuard => new(
            "WireGuard",
            "Install the wireguard-tools package in the workspace environment.",
            WireGuardPreflight),
        NetworkConnectionKind.OpenVpn => new(
            "OpenVPN",
            "Install the openvpn package in the workspace environment.",
            OpenVpnPreflight),
        NetworkConnectionKind.AnyConnect => new(
            "Cisco AnyConnect (OpenConnect)",
            "Install the openconnect package in the workspace environment.",
            AnyConnectPreflight),
        NetworkConnectionKind.Tailscale => new(
            "Tailscale",
            "Install tailscale and tailscaled in the workspace environment.",
            TailscalePreflight),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static IsolatedVpnConnectionPlan WireGuard(
        string directory,
        string interfaceName,
        byte[] configuration) =>
        new(
            "WireGuard",
            "Install the wireguard-tools package in the workspace environment.",
            WireGuardPreflight,
            WireGuardAttach,
            WireGuardHealth,
            WireGuardCleanup,
            [directory, interfaceName],
            [interfaceName],
            [directory, interfaceName],
            [new IsolatedVpnSecretFile($"{interfaceName}.conf", configuration)],
            StandardInput: null);

    public static IsolatedVpnConnectionPlan OpenVpn(
        string directory,
        string interfaceName,
        byte[] configuration) =>
        new(
            "OpenVPN",
            "Install the openvpn package in the workspace environment.",
            OpenVpnPreflight,
            OpenVpnAttach,
            OpenVpnHealth,
            OpenVpnCleanup,
            [directory, interfaceName],
            [directory, interfaceName],
            [directory, interfaceName],
            [new IsolatedVpnSecretFile("openvpn.conf", configuration)],
            StandardInput: null);

    public static IsolatedVpnConnectionPlan AnyConnect(
        NetworkConnectionConfiguration.AnyConnect configuration,
        string directory,
        string interfaceName,
        byte[]? password,
        byte[]? clientCertificate)
    {
        var certificatePath = clientCertificate is null
            ? null
            : $"{directory}/client-certificate";
        var arguments = new List<string>();
        if (configuration.Username is not null)
        {
            arguments.Add("--user");
            arguments.Add(configuration.Username);
        }

        if (configuration.AuthenticationGroup is not null)
        {
            arguments.Add("--authgroup");
            arguments.Add(configuration.AuthenticationGroup);
        }

        if (certificatePath is not null)
        {
            arguments.Add("--certificate");
            arguments.Add(certificatePath);
        }

        if (password is not null)
        {
            arguments.Add("--passwd-on-stdin");
        }

        arguments.Add(configuration.Gateway.AbsoluteUri);
        var secretFiles = clientCertificate is null
            ? Array.Empty<IsolatedVpnSecretFile>()
            : [new IsolatedVpnSecretFile("client-certificate", clientCertificate)];
        return new IsolatedVpnConnectionPlan(
            "Cisco AnyConnect (OpenConnect)",
            "Install the openconnect package in the workspace environment.",
            AnyConnectPreflight,
            AnyConnectAttach,
            AnyConnectHealth,
            AnyConnectCleanup,
            [directory, interfaceName, .. arguments],
            [directory, interfaceName],
            [directory, interfaceName],
            secretFiles,
            password);
    }

    public static IsolatedVpnConnectionPlan Tailscale(
        NetworkConnectionConfiguration.Tailscale configuration,
        string directory,
        string interfaceName,
        byte[]? authKey)
    {
        var authPath = authKey is null ? string.Empty : $"{directory}/auth-key";
        var secretFiles = authKey is null
            ? Array.Empty<IsolatedVpnSecretFile>()
            : [new IsolatedVpnSecretFile("auth-key", authKey)];
        return new IsolatedVpnConnectionPlan(
            "Tailscale",
            "Install tailscale and tailscaled in the workspace environment.",
            TailscalePreflight,
            TailscaleAttach,
            TailscaleHealth,
            TailscaleCleanup,
            [
                directory,
                interfaceName,
                configuration.ExitNode,
                configuration.ControlServer?.AbsoluteUri ?? string.Empty,
                authPath,
            ],
            [directory, interfaceName],
            [directory, interfaceName],
            secretFiles,
            StandardInput: null);
    }
}
