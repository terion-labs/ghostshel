# ADR 0017: Native .NET agent runtime

- Status: Accepted
- Date: 2026-07-23
- Supersedes: [ADR 0005](0005-agent-sidecar-and-capability-broker.md)

## Context

The Pi project remains a useful reference for agent-session lifecycle, provider
streaming, steering, compaction, and tool calls. Running it in GhostSHELL would,
however, require a Node.js child process solely for the built-in agent. That
would add another runtime, package supply chain, process supervisor, IPC
protocol, upgrade path, failure domain, and idle memory cost to every desktop
installation.

Desktop v1 already runs its session host in-process behind protocol-shaped
application contracts. Provider/model flexibility does not require the agent
loop itself to live in another process, and process isolation would not replace
the capability checks that must occur at the session-host execution boundary.

## Decision

Implement the first agent runtime natively in .NET. The loop is a real
`GhostShell.Agent` boundary that owns conversation state, provider streaming,
tool-call assembly, steering, compaction, and run cancellation. It depends on
provider-neutral Core primitives, not Avalonia controls, terminal engines,
provider SDK payloads, or persisted vendor session formats.

Provider adapters parse each provider's external stream into the native loop's
small typed event model. Provider credentials are resolved from `SecretRef`
only inside the provider-adapter boundary and are never added to the
conversation or tool results. Official provider SDKs or direct HTTP clients may
be used privately when they reduce compatibility risk; their request and
response types do not cross the adapter.

Internal GhostSHELL operation names remain stable domain and audit identities;
they are not assumed to satisfy a model provider's tool-name grammar. Each tool
definition therefore carries a separate provider name limited to 64 ASCII
letters, digits, underscores, or hyphens. Already compatible names, including
run-local MCP aliases, remain unchanged. Other internal names receive a
deterministic collision-resistant opaque alias. The reducer accepts only the
exact provider-name map frozen for that turn and translates a returned call
back to its internal operation before any proposal reaches orchestration.
Provider request history replays the exact retained alias. A bounded
session-owned alias ledger rejects any attempt to bind an alias to a different
internal operation across later turns, tool continuations, cancellation, or
compaction. Malformed Unicode, per-manifest collisions, cross-turn rebinding,
and session-capacity overflow all fail before provider invocation.

The loop cannot execute application tools. A model tool call is an untrusted
proposal correlated to an authenticated agent run. GhostSHELL resolves its
exact target, policy, risk, approval, and one-action authorization before the
session host invokes a typed application operation. The host records the
requested decision and terminal outcome durably. Provider and run cancellation
share one whole-turn boundary. Each authorized tool dispatch additionally owns
a linked one-action cancellation boundary; cancelling only that action returns
a structured cancelled tool result and does not revoke the run.

Desktop v1 exposes no agent IPC endpoint and grants no ambient authority to
other same-account processes. A future standalone or headless host must add an
authenticated transport and its own identity ADR without weakening the same
target, policy, approval, audit, and execution contracts.

Pi remains a behavior and test reference only. GhostSHELL does not package
Node.js, launch Pi, consume Pi session files, or depend on TypeScript types.

The foundational loop deliberately owns no provider transport or tool
authority. `GhostShell.Agent` references only Core primitives and the BCL. It
implements strict bounded stream reduction, stable transcript validation,
generation-fenced cancellation, bounded non-cooperative provider work,
CAS-based compaction, cursor resynchronization, cloned data-only tool
proposals, and structured tool-result continuation. Cancelling pending
proposals rolls their unexecuted turn back. Provider-turn budgets distinguish
at most 128 advertised tool definitions from at most 16 returned calls, and
bound schemas independently from generated call arguments.

[ADR 0037](0037-bounded-native-provider-steering.md) adds the first steering
slice without widening that boundary. One human update may replace only the
actively streaming initial user generation before commit. The kernel
linearizes Steer against commit and cancellation, reserves bounded replacement
provider capacity, retains the exact provider/tool manifest and one revised
user message, and generation-fences a non-cooperative old stream. The governed
runtime first revalidates the pinned target, provider revision, policy
generation, and run lifecycle. Steering creates no permit, SessionHost
operation, authority decision, or audit row and is unavailable during tool
continuations, questions, capability decisions, approvals, and tool execution.

[ADR 0018](0018-native-ai-provider-and-chat-boundary.md) adds provider I/O in a
separate native project. Secret resolution remains request-local to that
adapter. A provider profile is pinned by immutable catalog revision for an
agent run; editing, disabling, or removing it invalidates the binding before
any retained transcript can be sent again.

`GhostShell.Agent.Runtime` is the provider-neutral orchestration boundary. It
references the agent kernel plus application contracts, but no provider,
terminal-engine, platform, vault, or UI implementation. It accepts an exact
panel, the current live tab, or the current workspace as the run target and
pins the initial ordered terminal/session membership. A bounded,
host-generated system manifest describes only those terminals and their
capabilities; its titles, connection labels, and working directories remain
explicitly untrusted. Multi-terminal tool schemas require a capability-specific
`panel_id`. For every proposal the runtime freshly resolves the target,
revalidates the fixed membership and selected panel, converts the request into
an exact panel/session action, waits for the capability broker, invokes the
session-host consume-and-execute bridge, redacts and bounds the result, and
returns the trusted panel ID with the correlated structured result to the same
request-scoped provider adapter. Scope or session drift fails closed and
requires a new run. Parallel calls fail closed; tool rounds and total turn
lifetime are bounded. A linked, identity-tracked cancellation source exists
only while one authorized terminal dispatch is active. Its one-shot
cancellation state is projected with that activity; completion clears the
identity before disposing it, so stale completion and duplicate-cancel races
cannot affect a later action. Whole-turn cancellation still takes precedence
and revokes run authority.

That same ordered membership is projected as immutable presentation-only rows
for the visible desktop context inspector. Exact identities and host-verified
operations remain distinct from bounded, redacted, explicitly untrusted display
metadata. The rows are cleared with the run and cannot be consumed as
authorization.

If execution finishes but the terminal-outcome audit remains unavailable after
the host's exact-completion retry, the runtime never submits that result for
provider continuation. It cancels the run and reports the stable recovery
failure instead. Retrying a completion cannot redispatch its terminal side
effect.

The desktop binds that runtime to one composition-owned human approval
principal and presents a visible `Active terminal` / `Current tab` /
`Workspace` selector, streaming state, active operations, one-action
approvals, per-action cancellation, a separate persistent run-wide Stop,
failure recovery, and renderer capability limits.
The selector cannot retarget an existing run, and a broad-scope proposal still
shows and authorizes its exact narrowed panel action. Run-local YOLO remains
deliberately exact-panel-only. A provider adapter still never receives a
broker, session-host client, terminal object, or executor.

## Consequences

- Desktop packaging and lifecycle remain within the existing .NET process.
- Cancellation, telemetry correlation, and session-host calls do not cross an
  extra IPC boundary.
- Provider adapters can be added independently while policy and tools remain
  provider-neutral.
- GhostSHELL owns the correctness of streaming assembly, tool-call sequencing,
  steering, and compaction and must test those behaviors directly.
- Provider configuration changes invalidate a live run instead of silently
  changing endpoint, model, or credential scope under an existing transcript.
- The governed runtime can be replaced by a future headless presentation
  without moving provider or execution authority into the agent kernel.
- Moving the loop out of process later remains possible behind the application
  contracts, but isolation is not treated as authorization.

## Alternatives rejected

- A Pi/Node.js child process adds a runtime and protocol solely for the agent
  without removing any GhostSHELL security responsibility.
- Letting provider adapters call terminal, browser, file, or MCP operations
  directly creates a hidden control plane.
- Binding Core, Application, or Protocol to one provider SDK makes provider
  fallback and local-model support expensive.
- Treating same-user local processes as trusted would create an undocumented
  desktop control surface and complicate future headless authentication.
