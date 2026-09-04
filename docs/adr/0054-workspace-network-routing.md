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

The application stores one `NetworkPolicy`, but its available connections are derived from the
global network-connection catalog rather than maintained as a second allow-list. A workspace
stores either no override, which inherits that application policy and the global catalog, or one
complete replacement with its own connection subset. A policy also contains a remembered
selection, an enabled flag, and a kill-switch flag. Only one connection can be active. Connection
chaining and ordered fallback are not part of this design.

Disabling networking uses the direct route and retains the remembered selection. If an enabled
connection fails and the kill switch is off, the workspace may use its direct route. If the
kill switch is on, the workspace exposes blocked egress until the selected connection works or
the user disables the policy.

Each provider starts in one of two placements:

- An isolated workspace can attach a VPN inside its persistent Linux environment. Host-side
  consumers such as CEF reach that environment through a loopback-only proxy owned by the
  workspace runtime. For proxy profiles, the isolate installs a fail-closed nftables output
  policy and transparently sends guest IPv4 TCP through `redsocks`. DNS is changed to public
  recursive resolvers, UDP/53 is answered by `dnstc` so clients retry over the intercepted TCP
  path, and other UDP is rejected. HTTPS proxy transport is wrapped by `socat` with certificate
  and hostname verification. IPv6 and non-DNS UDP are blocked rather than sent directly because
  HTTP CONNECT cannot carry them and SOCKS5 UDP support cannot be assumed for every server. This
  DNS design requires the upstream proxy to allow TCP connections to port 53; route startup probes
  that path and reports a specific, fail-closed error when the proxy denies it.
- A non-isolated workspace can use a proxy without changing host routes. GhostSHELL injects
  proxy settings into processes it owns and applies the proxy to in-process clients. WireGuard,
  AnyConnect, and Tailscale can also expose an app-scoped SOCKS5 route through a userspace client;
  those transports do not create a host interface or change the host routing table.

`IWorkspaceNetworkRuntime` owns one running workspace route. It resolves the selected provider,
starts and stops its session, applies kill-switch fallback, and publishes a
`WorkspaceNetworkSnapshot`. Panels consume only the resulting workspace egress through the
existing `WorkspaceRuntimeServices` boundary. They do not know which provider produced it.

Peer-bound agent HTTP fetches resolve A and AAAA records with DNS-over-TLS carried through the
workspace connector to the literal resolver endpoints `1.1.1.1:853` and `1.0.0.1:853`. They never
call the host resolver. The safety policy rejects the entire result if any returned address is
non-public, then connects the request to an admitted IP address while `SocketsHttpHandler` retains
the original URI hostname for TLS identity. If both routed DNS-over-TLS endpoints are unavailable,
the fetch reports DNS failure; it does not retry with host DNS or a direct socket.

VPN sessions are not published as connected until an end-to-end reachability check succeeds.
Inside an isolate, the check runs `curl` against IP-literal HTTPS peers while binding the socket to
the selected tunnel interface. Host userspace VPNs perform the same class of TLS/HTTP check through
their loopback SOCKS route. The checks repeat while the session is active and publish a failed
connection if traffic stops even though the process, interface, or route entries still exist. Both
placements try `1.1.1.1` and `1.0.0.1`; probes never fall back to direct traffic or host DNS.

The window control reads that same snapshot. It can select any connection in the effective
policy and enable or disable networking. Starting, connected, failed, and kill-switch-blocked
states remain visible in the control.

## Provider mapping

- WireGuard uses `wireguard-tools` inside the workspace environment and `wireproxy` for a
  non-isolated workspace.
- OpenVPN uses the `openvpn` client inside the workspace environment. Non-isolated OpenVPN remains
  unavailable until an OpenVPN 3 Core adapter is connected to a userspace IP stack; the OpenVPN CLI
  alone cannot provide this boundary.
- AnyConnect uses `openconnect` inside the workspace environment. On macOS and Linux, a
  non-isolated workspace uses OpenConnect's script-tun mode with `ocproxy`. That userspace socket
  transport is unavailable on Windows.
- Tailscale uses a private `tailscaled` instance in userspace-networking mode and requires an exit
  node for full Internet routing. Non-isolated macOS and Linux workspaces keep a private,
  per-workspace identity; Windows host placement remains unavailable rather than reusing the
  system-wide Tailscale service.

The packaged `ubuntu:24.04` workspace image includes `nftables`, `redsocks`, `socat`,
`wireguard-tools`, `openvpn`, and `openconnect`. A dedicated `ghostshell-net` account owns the
proxy sidecars and is the only identity allowed to reach the configured proxy outside the
transparent redirect. Tailscale is not installed by an unaudited download script; a workspace
that selects Tailscale receives a concrete missing-runtime error until `tailscale` and
`tailscaled` are installed in its image.

## Consequences

Different workspaces can hold different simultaneous network identities. Provider failures have
one typed path to the window and the kill switch. Adding support to a panel means consuming the
workspace route, not adding provider-specific settings.

Application and workspace settings must reject references to missing network profiles. Deleting
a profile must also reject or repair policies that still reference it. Runtime state is never
written into durable definitions.

Each loopback route broker generates a cryptographically random credential for one live workspace.
Route-aware clients authenticate with SOCKS5 username/password. The embedded Chromium renderer uses
the same listener through HTTP CONNECT or authenticated HTTP forwarding because Chromium does not
support SOCKS5 username/password; its proxy challenge is answered from the credential held in app
memory. Unauthenticated clients and credentials from another workspace are rejected. Credentials
are redacted from object formatting and are not placed in browser proxy preferences or route keys.

For a non-isolated workspace, owned terminal and stdio MCP processes receive an authenticated proxy
URI in the standard upper- and lower-case proxy variables. `NO_PROXY` and `no_proxy` are removed, and
SSH terminal launches use the authenticated broker through an explicit `ProxyCommand`. This routes
software that honors those settings, but it is not a host security boundary: a child can ignore its
environment and open a direct socket, while another process running as the same OS user may inspect
that child's environment or GhostSHELL process memory. Universal enforcement of arbitrary child TCP
and UDP requires workspace isolation (where the guest egress firewall is authoritative) or a future
platform sandbox. The host kill switch is therefore authoritative for in-process connectors, not for
arbitrary non-isolated child binaries.

Peer-bound agent DNS and VPN reachability currently depend on Cloudflare's public resolver and
connectivity endpoints. A workspace route or upstream proxy that intentionally blocks TCP/853 will
make peer-bound agent fetches fail closed, and a route that blocks both HTTPS probe addresses will
be reported unhealthy even if some other destinations remain reachable. A later settings contract
may offer additional audited endpoints without introducing host-resolution bootstrap or direct
fallback.

SMB uses a per-session loopback relay whose upstream TCP/445 stream is opened by the workspace
connector. A version-checked SMBLibrary compatibility boundary preserves the logical server name used by
SMB negotiation. If that boundary is incompatible, or a routed session requires unsupported DFS
referrals, the operation fails before file access and never falls back to an untracked direct
socket.
