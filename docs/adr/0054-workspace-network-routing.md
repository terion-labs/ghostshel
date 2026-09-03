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

- An isolated workspace attaches the route inside its persistent Linux environment. Host-side
  consumers such as CEF reach that environment through a loopback-only proxy owned by the
  workspace runtime.
- A non-isolated workspace keeps networking app-scoped. Proxy settings are injected into
  processes owned by GhostSHELL and applied to in-process clients. VPN providers expose their
  userspace network through a loopback-only proxy. They do not create a host TUN device or
  change the host routing table.

`IWorkspaceNetworkRuntime` owns one running workspace route. It resolves the selected provider,
starts and stops its session, applies kill-switch fallback, and publishes a
`WorkspaceNetworkSnapshot`. Panels consume only the resulting workspace egress through the
existing `WorkspaceRuntimeServices` boundary. They do not know which provider produced it.

The window control reads that same snapshot. It can select any connection in the effective
policy and enable or disable networking. Starting, connected, failed, and kill-switch-blocked
states remain visible in the control.

## Provider mapping

- WireGuard uses a userspace WireGuard implementation and userspace IP stack outside isolation.
  Inside isolation it may attach the tunnel to that environment's network namespace.
- OpenVPN uses OpenVPN 3 Core. Outside isolation its packet backend connects to the userspace
  IP stack rather than an operating-system TUN device.
- AnyConnect uses `libopenconnect`. Outside isolation its packet backend connects to the same
  userspace IP stack.
- Tailscale uses userspace networking outside isolation and a persistent state directory per
  workspace. Full Internet routing requires an exit node.

## Consequences

Different workspaces can hold different simultaneous network identities. Provider failures have
one typed path to the window and the kill switch. Adding support to a panel means consuming the
workspace route, not adding provider-specific settings.

Application and workspace settings must reject references to missing network profiles. Deleting
a profile must also reject or repair policies that still reference it. Runtime state is never
written into durable definitions.
