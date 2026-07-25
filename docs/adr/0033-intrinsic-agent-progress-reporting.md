# ADR 0033: Intrinsic agent progress reporting

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0017](0017-native-dotnet-agent-runtime.md) and
  [ADR 0019](0019-one-action-agent-capability-broker.md)

## Context

A native agent turn can take several provider and governed-tool rounds. The
existing transcript, provisional assistant text, approval card, and active-tool
card explain final answers and broker-governed application actions, but they do
not give the model a bounded way to describe useful intermediate progress.

Treating a progress update as an ordinary application action would be
misleading. It does not read or mutate a terminal, browser, file system, or
workspace graph; it must not inherit a policy capability merely to cross the
capability broker. Conversely, accepting arbitrary model text as application
status would create an unbounded secret and persistence channel.

## Decision

The native provider tool surface always advertises the intrinsic
`agent.report_progress` tool after a run target has been resolved. It has a
closed object schema:

- `message` is required, nonblank, single-line, strict Unicode, contains no
  unsafe control or formatting code point, is at most 512 UTF-8 bytes, and
  must not look like a literal secret;
- `percent` is optional and, when present, is an integer from `0` through
  `100`;
- duplicate or unknown properties are rejected.

This tool is intentionally absent from `AgentToolCatalog`. That catalog
describes application actions which map to an `AgentCapability` and can enter
the broker/SessionHost authorization path. Progress reporting is a
presentation event: it requests no permit, consumes no authorization, invokes
no capability-bearing SessionHost action, and creates no action-audit record.
The runtime does use its existing read-only SessionHost context inspection to
re-resolve the pinned target before publishing. The intrinsic must not be
disguised as `Search`, terminal input, or another unrelated capability.

The runtime still re-resolves the exact pinned target before accepting an
update. Structural or operational scope drift, cancellation, disposal, and the
existing turn deadline fail closed. A valid update atomically replaces the
single nullable `CurrentProgress` value in the presentation snapshot; updates
never accumulate.

Progress text is labeled untrusted model output. It is displayed only in the
live presentation snapshot and is never copied into visible chat messages,
SQLite action audit, diagnostics, recovery, or logs. The provider receives the
fixed receipt `{"ok":true}` rather than an echo. Invalid input receives a
stable structured error and leaves the previous visible update unchanged.

`CurrentProgress` is cleared when a new prompt starts and when its turn
completes, fails, or is cancelled. Stop, clear, and runtime disposal also clear
it. This makes progress an ephemeral description of current work rather than a
claim about completed work.

The Avalonia agent surface renders the current update as one compact,
keyboard-focusable status card. A supplied percentage uses a determinate
progress bar; an omitted percentage remains indeterminate. The status is a
polite accessibility live region and clearly identifies the text as an agent
progress update.

## Consequences

- Long native-agent turns can communicate current work without a Node sidecar
  or software on the operated machine.
- Progress cannot expand run authority or bypass action policy.
- A malicious or mistaken model cannot use the status card as a durable
  transcript, audit, recovery, diagnostic, or logging channel.
- Only the newest bounded update is retained, and only while the turn remains
  active.

## Alternatives rejected

- Mapping progress onto `Search` would assign false capability and audit
  semantics to a presentation-only event.
- Sending progress through SessionHost would add a remote execution boundary
  where none is required.
- Appending progress to the chat transcript would make transient model status
  durable-looking and allow unbounded accumulation.
- Echoing the message in the tool receipt would duplicate untrusted content
  into the provider conversation.
