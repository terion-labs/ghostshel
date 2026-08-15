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

Provider-native reasoning continuity is retained through one internal bounded
replay-state value on the committed assistant message. An adapter may emit that
value only once, immediately before successful response completion; the
provider-neutral reducer never projects it to run events, UI, logs, or audit.
The binding covers the exact profile ID, provider identity, wire protocol,
model, actual routed endpoint, and stable adapter/auth route identity. For
vault-backed routes, that identity contains a one-way digest of the selected
opaque credential reference, never the reference itself or its value. Any
endpoint, credential-reference, authentication-route, profile, or protocol
drift fails closed before the transcript is serialized or sent. Replacing
material behind the same opaque reference is outside this check because the
current vault contract exposes no immutable credential revision.

A conversation is identified by its workspace-scoped run, not by its model.
The model is a per-turn routing choice: changing it while idle retains the
committed visible transcript and does not clear or fork the conversation. The
next request uses the selected model under the already-pinned provider profile
and authorization policy. Exact-model replay may retain provider-private signed
or encrypted reasoning artifacts. A same-route model change instead serializes
the visible assistant text and tool history without those model-bound opaque
artifacts, matching Pi's cross-model message transformation. This preserves the
human conversation without presenting incompatible provider state to the new
model.

Conversation maintenance follows Pi's context-budget behavior without adopting
its session format. Every model descriptor may publish a bounded context
window. After a successful turn, the kernel uses the latest provider-reported
total plus bounded estimates for any trailing messages, and compacts when that
usage exceeds `contextWindow - 16,384`. It retains approximately 20,000 tokens
of the newest complete user turns, summarizes only the older complete turns,
and rolls an existing summary forward. GhostSHELL never splits a structured
tool exchange merely to hit the token target. The summarizer receives a
prompt-injection-resistant structured checkpoint contract derived from Pi's
Goal, Constraints, Progress, Decisions, Next Steps, and Critical Context
sections. Compaction remains revision-fenced and optional maintenance: a
maintenance-provider failure cannot discard the answer that already completed.

The compaction route and optional conversation-title route are independent
provider/model selections in the global AI configuration. Workspace and saved
screen policy layers may override either route independently. An unspecified
compaction route uses the resolved global primary model; an unspecified title
route keeps the deterministic first-user-message title and performs no extra
provider request. Main-window and Quick Terminal conversations both consume
the saved global policy while retaining separate workspace-scoped transcripts.
The visible composer projects current usage and the active
model's effective context budget. These maintenance routes are data-processing
choices, not execution authority, and never receive agent tools.

Anthropic adapters retain the exact ordered signed `thinking`, opaque
`redacted_thinking`, text, and tool-use blocks so a tool result can immediately
continue a signed turn without exposing hidden thinking. OpenAI Responses
requests `reasoning.encrypted_content`, retains finalized response items and
their output/tool slots, and backfills encrypted reasoning from the completed
response when the output-item event omitted it. Both formats enforce strict
item, aggregate byte, JSON-depth/node, slot-contiguity, and duplicate bounds.
Other adapters receive no replay-state surface.

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

[ADR 0043](0043-idle-native-agent-checkpoints.md) adds a deliberately narrower
durability boundary than Pi's operation harness: only fully committed `Ready`
conversation state can be checkpointed. Active provider streams, pending tool
decisions, compaction leases, approvals, capabilities, authorities, provider
clients, and credentials remain process-local. A versioned kernel-owned JSON
payload is stored behind an application port by a revision-fenced,
integrity-checked SQLite adapter. Restore returns an idle kernel and never
infers or replays an unfinished external effect. The desktop orchestration saves
settled turns and restores only within the same workspace identity.

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
terminal-engine, platform, vault, or UI implementation. The desktop binds a
run to one workspace identity. A bounded host-generated system manifest is
assembled by the runtime's registered tool-family contributions and rebuilt
after each tool round from the current supported live panels. The current
registry is runtime-owned; panels contribute eligibility and current context,
not executable plugin objects. Panel titles, connection
labels, and working directories remain explicitly untrusted; every operational
schema narrows authority with a host-enumerated `panel_id` where needed. For
every proposal the runtime freshly resolves the target and selected panel,
converts the request into an exact panel/session action, waits for the capability
broker, invokes the session-host consume-and-execute bridge, redacts and bounds
the result, and returns the trusted panel ID with the correlated structured
result to the same request-scoped provider adapter. Returned tool proposals are
executed sequentially in provider order and submitted as one correlated result
set; an uncertain mutation outcome revokes the run and skips the remainder.
Exact internal targets retain fixed-membership fail-closed semantics. Tool
rounds and total turn lifetime are bounded. A linked, identity-tracked
cancellation source exists
only while one authorized terminal dispatch is active. Its one-shot
cancellation state is projected with that activity; completion clears the
identity before disposing it, so stale completion and duplicate-cancel races
cannot affect a later action. Whole-turn cancellation still takes precedence
and revokes run authority.

That same current ordered Workspace topology is projected as immutable
presentation-only rows for the visible desktop context inspector. The snapshot
is replaced after each successful topology refresh; internal exact/selected
targets retain fixed rows. Exact identities and host-verified operations remain
distinct from bounded, redacted, explicitly untrusted display metadata. The
rows are cleared with the run and cannot be consumed as authorization.

If execution finishes but the terminal-outcome audit remains unavailable after
the host's exact-completion retry, the runtime never submits that result for
provider continuation. It cancels the run and reports the stable recovery
failure instead. Retrying a completion cannot redispatch its terminal side
effect.

The desktop binds that runtime to one composition-owned human approval
principal and presents a visible `Workspace` scope, streaming state, active
operations, one-action
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
