# ADR 0054: workspace network routing

## Status

Accepted.

## Context

GhostSHELL needs application networking defaults and complete per-workspace overrides.
A workspace can offer several proxy or VPN connections, but it sends traffic through one
selected connection at a time. The user must be able to select that connection, disable it,
and enable a kill switch from the window that owns the workspace.

The routing rule applies to every workspace backend. This includes terminals, browsers,
SSH and SFTP, file providers, databases, Docker, Git, monitors, MCP servers, and an AI agent
when that agent is configured to use the workspace environment.

## Decision

Network connections are reusable durable definitions. The first supported configurations are:

- SOCKS5, HTTP, and HTTPS proxies;
- WireGuard;
- OpenVPN;
- Cisco AnyConnect through OpenConnect;
- Tailscale through a selected exit node.

Configuration payloads and credentials that can contain secrets are stored in the secret
vault. Durable definitions contain only `SecretRef` values.

The application stores one complete `NetworkPolicy`. A workspace stores either no override,
which inherits that policy, or one complete replacement. A policy contains an ordered set of
available connections, a remembered selection, an enabled flag, and a kill-switch flag.
Only one connection can be active. Connection chaining and ordered fallback are not part of
this design.

Disabling networking uses the direct route and retains the remembered selection. If an enabled
connection fails and the kill switch is off, the workspace may use its direct route. If the
kill switch is on, the workspace exposes blocked egress until the selected connection works or
the user disables the policy.

Each provider starts in one of two placements:

- An isolated workspace can attach a VPN inside its persistent Linux environment. Host-side
  consumers such as CEF reach that environment through a loopback-only proxy owned by the
  workspace runtime. Proxy profiles are rejected for isolated workspaces until the guest can
  enforce them for every process.
- A non-isolated workspace can use a proxy without changing host routes. GhostSHELL injects
  proxy settings into processes it owns and applies the proxy to in-process clients. VPN profiles
  are rejected outside isolation until their userspace transports are implemented.

`IWorkspaceNetworkRuntime` owns one running workspace route. It resolves the selected provider,
starts and stops its session, applies kill-switch fallback, and publishes a
`WorkspaceNetworkSnapshot`. Panels consume only the resulting workspace egress through the
existing `WorkspaceRuntimeServices` boundary. They do not know which provider produced it.

The window control reads that same snapshot. It can select any connection in the effective
policy and enable or disable networking. Starting, connected, failed, and kill-switch-blocked
states remain visible in the control.

## Provider mapping

- WireGuard uses `wireguard-tools` inside the workspace environment.
- OpenVPN uses the `openvpn` client inside the workspace environment.
- AnyConnect uses `openconnect` inside the workspace environment.
- Tailscale uses `tailscaled` inside the workspace environment and requires an exit node for
  full Internet routing.

Host-side userspace implementations remain future work. They must not create a host TUN device
or change the host routing table.

The packaged `ubuntu:24.04` workspace image includes `nftables`, `wireguard-tools`, `openvpn`,
and `openconnect`. Tailscale is not installed by an unaudited download script; a workspace that
selects Tailscale receives a concrete missing-runtime error until `tailscale` and `tailscaled`
are installed in its image.

## Consequences

Different workspaces can hold different simultaneous network identities. Provider failures have
one typed path to the window and the kill switch. Adding support to a panel means consuming the
workspace route, not adding provider-specific settings.

Application and workspace settings must reject references to missing network profiles. Deleting
a profile must also reject or repair policies that still reference it. Runtime state is never
written into durable definitions.

The loopback SOCKS adapters currently rely on the operating system's same-host loopback boundary
and do not authenticate clients. A future authenticated broker is required before treating other
processes running as the same host user as untrusted clients; the listener must remain loopback-only
until that broker exists.

The current SMB client library opens TCP/445 itself and has no stream or proxy injection point.
SMB therefore fails before credential access whenever a workspace route is not direct. Routed SMB
requires a connector-capable SMB transport; it must never fall back to an untracked direct socket.
