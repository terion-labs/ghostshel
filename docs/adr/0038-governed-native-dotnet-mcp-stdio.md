# ADR 0038: Governed native .NET MCP stdio boundary

- Status: Accepted
- Date: 2026-07-25
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md),
  [ADR 0018](0018-native-ai-provider-and-chat-boundary.md), and
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)
- Protocol basis:
  [MCP 2025-11-25 lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle),
  [stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports#stdio),
  and [tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)

## Context

MCP is useful only if it does not become a second, less-governed execution
path. A configured server is an external process that can expose changing
tool names and schemas, emit hostile metadata and results, receive secrets in
its environment, and complete a side effect after its client loses the
response. Passing SDK tool objects directly to the provider would bypass
GhostSHELL's target, policy, approval, one-action authorization, cancellation,
and audit boundaries.

Pi remains a behavior reference only. GhostSHELL's provider loop and MCP
client stay native .NET; the application does not launch a Node.js process for
its agent runtime. A user may separately configure any MCP server executable,
including one implemented with Node.js, but that optional server is an
explicitly managed integration rather than an application dependency.

The protocol is evolving. The first production slice needs a small,
version-pinned surface rather than speculative support for every transport,
server-to-client feature, or draft revision.

## Decision

GhostSHELL adds a native .NET, stdio-only MCP client boundary in
`GhostShell.Mcp`. It pins the official `ModelContextProtocol.Core` SDK at
`1.3.0`. The SDK's `McpClient`, initialization options, protocol DTOs, typed
tool requests, JSON-RPC correlation, and lifecycle handling remain private to
that project. Application and runtime callers receive only closed GhostSHELL
contracts. `AgentMcpSessionHost` is the only exported production type from the
assembly; the low-level client, launch object, plaintext environment,
transport options, and result DTOs are internal and cannot form a second
in-process execution path.

GhostSHELL deliberately does not use the SDK's built-in
`StdioClientTransport`. In `1.3.0` that transport starts from the complete
ambient process environment and then augments it, and it does not expose the
pre-deserialization message, JSON-shape, and retained-stderr bounds required by
this boundary. `GhostShell.Mcp` instead supplies an internal bounded
`IClientTransport`/`ITransport` implementation to the official `McpClient`.
That transport owns direct process launch, environment clearing, newline
framing, strict UTF-8 and JSON-shape validation, bounded stderr draining, and
bounded shutdown. This is a transport adapter, not a second MCP lifecycle or
tools implementation.

The first slice supports the stable MCP `2025-11-25` lifecycle:

- direct subprocess launch without a shell, through the GhostSHELL transport;
- bounded newline-delimited UTF-8 JSON-RPC over stdin/stdout, parsed into the
  official SDK protocol types;
- a cumulative incoming control-message budget applied to initialization and
  reset for each tool-list page and tool call; exceeding it closes the transport instead of
  allowing notifications or server requests to starve the expected response;
- `initialize`, `notifications/initialized`, paged `tools/list`, and
  `tools/call`;
- protocol ping handling supplied by the SDK; and
- bounded, count-only stderr diagnostics.

Roots, resources, prompts, sampling, elicitation, tasks, server-initiated
application actions, notifications that expand authority, Streamable HTTP,
legacy SSE, and draft protocol revisions are not advertised or accepted by
this slice.

### Durable server profile

One immutable `McpServerProfile` contains:

- a random `McpServerProfileId`, schema version, revisioned durable identity,
  name, and enabled state;
- one direct executable plus an ordered bounded argument list;
- an optional working directory;
- environment-variable names whose values are only opaque `SecretRef`
  references; and
- an exact case-sensitive allowlist of enabled MCP tool names.

There are no literal environment values, shell command strings, redirections,
pipelines, command substitution, startup scripts, or ambient secret values in
the definition. Credential-shaped literal material is rejected from profile
text, argv, secret-reference IDs, enabled-tool names, and tool-call arguments.
Imported MCP profiles are always quarantined as disabled and require the same
explicit trust review as a newly enabled profile. The child process starts
with environment inheritance disabled. GhostSHELL supplies only the profile's
explicitly configured, vault-resolved values; a server that needs `PATH`,
`DOTNET_ROOT`, or another runtime variable must declare its corresponding
secret reference. Each reference is resolved at process creation with
`SecretScopeKind.McpServer` and `SecretUseKind.McpServerEnvironment` for that
exact profile ID. Per-value, aggregate UTF-8, and cross-platform environment
block budgets are enforced before launch. Values, arguments derived from
values, and child environment contents never enter definitions, import/export,
recovery, diagnostics, action audit, or normal logs.

Adding a profile, changing its executable, arguments, working directory, or
environment bindings, and expanding the enabled-tool allowlist requires a
separate authenticated confirmation in Settings. Disabling, removing, or
narrowing a profile does not require expansion confirmation but invalidates
new discovery. Any MCP profile add, edit, disable, remove, import, or reload
rotates a host-owned catalog generation. Runs pinned to an absent, disabled, or
different revision are synchronously marked closing and their directly
launched processes are disposed asynchronously under the normal cleanup bound.

The Settings **Test** operation requires the composition-owned authenticated
human principal, an enabled profile, and the exact current profile revision.
It serializes one probe under the caller's deadline, capped at 30 seconds,
resolves that profile's environment `SecretRef` values, starts the bounded
child, performs initialization and complete bounded tool discovery, reports
only discovered and enabled counts, and explicitly disposes the probe session
before returning. Server-chosen tool identifiers are withheld. It does not
call a server tool, create broker or agent-action authority, retain stderr or
log content, reconnect, or establish persistent health polling.

Schema one does not persist trust provenance separately from enablement.
Consequently, a trusted-but-disabled profile cannot yet be probed, because
allowing Test for every disabled profile would also make an imported,
unreviewed executable launchable. Separating those states is a later schema
decision.

### Frozen run manifest

MCP is available to a governed run only when its effective `McpTools`
permission is `Ask` or `Auto` and no YOLO overlay is active. `Off` starts no
server and advertises no MCP tool. `Auto` still requires human approval
because every first-slice MCP call is conservatively classified as a
mutation. `YOLO` never authorizes an MCP call.

Before catalog, vault, or process access, the MCP boundary acquires a
broker-issued launch lease for the exact registered run, agent actor, live
policy generation, and `Ask`/`Auto` MCP permission. Suspension, cancellation,
YOLO activation, policy replacement, or actor/run mismatch revokes or denies
that lease. At first use for an authorized run, the MCP boundary opens each
eligible enabled profile, lists all bounded pages, intersects the result with
the configured exact allowlist, and freezes:

- profile ID and durable revision;
- negotiated protocol and server identity;
- the approved allowlist spelling for display, or an opaque redacted label
  when that spelling collides with a resolved secret;
- a private per-session HMAC identity bound to the original case-sensitive
  protocol tool name;
- an exact bounded object input schema with annotation fields removed, plus its
  digest; and
- a 64-character, provider-compatible, run-local opaque alias owned by
  GhostSHELL.

Only the aliases and schemas in that frozen manifest enter the provider tool
set. The raw protocol tool name exists only in the private session binding used
for the eventual SDK call and is never placed in diagnostics or provider
context. MCP names, annotations, descriptions, and schemas are untrusted data;
they cannot add a catalog capability, choose a risk, change target or policy,
or supply approval text. A duplicate alias, duplicate server tool, invalid
schema, unsupported required task execution, excessive page/tool/schema or
cumulative discovered-tool budget, profile drift, tool-list change, or manifest
mismatch fails the MCP surface closed. Unused server instructions, tool titles,
descriptions, and output schemas are bounded but not retained. A server
notification or later re-list may invalidate a manifest but can never expand
the current run. The user must clear the run and accept a newly discovered
manifest.

Individually valid sanitized schemas also share a 512-KiB aggregate run budget.
Discovery returns `mcp_schema_capacity_exceeded` and disposes every opened
profile session before a larger manifest can be retained or handed to the
provider-neutral agent kernel. The kernel independently caps one provider turn
at 128 tool definitions, 64 KiB per schema, and 512 KiB across schemas; its
16-call response limit remains a separate model-output bound.

Replacing or deleting a profile-scoped MCP credential invalidates every run
that resolved it and synchronizes with any in-flight Settings probe before the
management operation returns. The next run or probe resolves the new value;
the prior child cannot remain an authorized credential holder.

### Trusted composition and one-action execution

The provider calls one frozen alias with one bounded JSON object. The trusted
runtime maps the alias back to its exact profile, tool, schema digest, and run
manifest. It canonicalizes and bounds the arguments once. The application
composer binds these values together with the existing run, target
fingerprint, actor, policy generation, and action deadline into:

- the generic trusted catalog action `mcp.call`;
- an argument digest covering profile revision, manifest digest, opaque tool
  identity, schema digest, and canonical arguments; and
- a human approval presentation containing trusted server identity, the
  allowlisted tool display name or explicit redacted-label state, the exact
  effective process working directory, and the complete reversible bounded
  arguments.

The broker evaluates `mcp.call` as `McpTools` plus `Mutation`. After explicit
human approval, the MCP execution host freshly verifies the profile revision
and complete manifest, consumes the one-action authorization, and accepts
only `AgentAuthorizationSource.HumanApproval`. It then sends exactly one
`tools/call`. The provider runtime never receives the vault, process, MCP SDK
client, authorization consumer, or a generic executor.

There is no automatic retry. Calling the SDK is the conservative dispatch
commit point because the public client API cannot prove whether a failed or
cancelled request reached the server. Cancellation before that point is a
normal cancellation. Any timeout, transport failure, process exit, malformed
response, or cancellation after dispatch returns
`mcp_tool_outcome_unknown`, completion-audits that result, revokes the run,
and prevents provider continuation. An unconfirmed completion audit follows
the existing `agent_completion_audit_unavailable` quarantine path and never
replays the server call.

### Result and diagnostic boundary

Tool results are hostile. The execution host accepts only bounded MCP tool
result shapes, projects supported text and structured JSON into a maximum
64-KiB provider result, removes or reports unsupported binary/resource
content, redacts literal-secret-shaped text, validates strict Unicode, and
labels the envelope:

```text
content_origin=untrusted_mcp
```

The projection does not include executable paths, working directories,
environment names or values, profile secret references, stderr, server
instructions, protocol IDs, action/approval/authorization IDs, or raw
exceptions. Audit records only trusted action identity, exact digests, stable
outcome, timing, and bounded counts. The first slice drains stderr but retains
only count, truncation, and read-failure metadata; it does not retain stderr
text. A later diagnostic surface requires a separate decision before it can
retain or display any bounded, redacted tail.

### Lifetime and future transports

Stop, Clear, disposal, run cancellation, and the runtime's fail-closed run
termination close each directly launched run-owned MCP process. Durable MCP
catalog changes proactively revoke and dispose every affected idle or active
run; tool-list, policy, or target drift is also rejected at the next relevant
runtime or host check and follows the same run-termination path.
Process shutdown is bounded; the transport may request best-effort process-tree
termination for a non-cooperative owned root after its grace period. If root
cleanup cannot be confirmed, a sticky circuit breaker prevents later Settings
tests and run launches for the lifetime of that host.

This is process ownership, not an OS sandbox or a portable descendant
containment guarantee. A configured server runs with the desktop user's
authority and can deliberately detach or reparent descendants beyond what
`Process.Kill(entireProcessTree: true)` can prove. Users must therefore trust
the configured executable itself; environment isolation protects unrelated
ambient values but cannot constrain an intentionally malicious executable. An
MCP process is never treated as software installed on, or authority delegated
to, the remote machine behind a terminal.

Per-server scope selection, HTTP transport, reconnect after a dispatched
call, resources, prompts, sampling, notifications, tasks, per-tool risk
tuning, and headless/ACP/A2A decision routing require later ADR extensions.
The existing global/workspace/screen/run policy chain already scopes the
first-slice `McpTools` capability; no future non-desktop client may infer
approval from the absence of the desktop UI.

## Consequences

- MCP becomes a production-reachable extension point without changing the
  native in-process provider loop or adding a Node/Pi sidecar.
- Every MCP call uses the same human approval, one-use authorization, audit,
  cancellation, and fail-closed continuation rules as built-in actions.
- A malicious server can return prompt injection or lie about annotations,
  but it cannot expand the frozen run manifest or classify itself as safe.
- Conservative Ask-only behavior and outcome-unknown quarantine trade
  convenience for deterministic authority and no accidental replay.
- The official SDK owns MCP initialization, JSON-RPC correlation, lifecycle,
  and typed tool message semantics. GhostSHELL's SDK transport owns subprocess
  launch, framing bounds, environment isolation, stderr draining, and cleanup;
  the rest of GhostSHELL owns configuration trust, secret resolution, manifest
  pinning, policy, authorization, audit, and result projection.

## Alternatives rejected

- Passing `McpClientTool` objects directly to the provider would bypass the
  trusted catalog and material-argument approval boundary.
- Treating tool annotations as trusted risk metadata would let a server
  authorize itself.
- Inheriting the desktop process environment would disclose unrelated
  credentials and configuration.
- Retrying a timed-out or disconnected `tools/call` could repeat an already
  committed side effect.
- Supporting HTTP, sampling, resources, prompts, tasks, and list-change
  expansion in the first slice would create authority surfaces without a
  product decision or UI.
- Hand-writing a parallel MCP lifecycle/tools client would duplicate a
  maintained official native .NET SDK at a security-sensitive boundary. A
  small custom SDK transport is retained because the built-in process transport
  cannot meet the no-inheritance and bounded-input requirements.
