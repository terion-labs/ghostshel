# ADR 0045: Governed workspace layout mutations

## Status

Accepted — 2026-08-15.

## Context

An agent run is already pinned to one desktop window and workspace. Agents need
to create and close tabs and add, split, and close panels without gaining a
general-purpose Avalonia or docking API. Layout state is presentation-owned,
while authorization, scope, audit, and the authoritative graph are
SessionHost-owned.

## Decision

Expose seven closed tools: `connections.list`, `tab.create`, `tab.close`,
`panel.add`, `panel.split`, `panel.close`, and `panel.connect`. They are
available only to a complete `AgentTarget.Workspace`. Connection discovery
uses `Search`; create/add/split/connect use the append-only `WorkspaceLayout`
capability. Close is destructive.

The provider-facing built-in tool manifest is fixed for the run. Live panel
IDs, capabilities, and connection availability never rewrite schemas. Broad
panel tools accept one bounded `panel_id`; graph and connection observations
return current values as data, and every invocation resolves them against a
fresh host context. No workspace ID is accepted. A
typed composer binds the exact ordered graph topology and canonical arguments.
SessionHost revalidates that binding adjacent to one-action permit consumption,
then releases its graph lock before calling a narrow workspace-specific UI
mutation port so the UI can publish the replacement graph without deadlock.

The UI executes existing layout lifecycle paths with graph-conflict retry
disabled. Close may perform the existing active-session confirmation/force
path after destructive authorization, but unsaved database edits fail with
`workspace_layout_unsaved_changes`. The port returns a fresh graph and exact
created/closed identities. SessionHost compares that receipt with its own
authoritative graph before reporting success.

Crossing the UI mutation call is the commit boundary. Cancellation, transport
failure, or an unverifiable receipt after that boundary produces
`workspace_layout_outcome_unknown`; the action is not retried. Because the
workspace graph is local and remains observable, this is returned as a
non-retryable tool failure and provider continuation remains available for a
fresh `workspace.inspect`. A later authoritative graph revision is accepted
when it still contains the exact applied effect; panel startup must not turn a
successful layout mutation into an unknown outcome. Human layout changes
before dispatch produce `target_changed`.

`connections.list` returns at most 64 opaque workspace-port references with a
bounded display name, kind, and compatible panel kinds. It never returns
durable connection IDs, endpoints, usernames, paths, or credentials. Each
reference is bound to the current saved-definition revision and is invalidated
when that definition disappears or changes. `panel.connect` accepts only such
a reference and an in-workspace panel ID. Create/add/split may accept one
compatible reference to select the new panel's connection; terminal creation
requires it. There is no first/local/default terminal connection path.

## Consequences

- The model cannot discover or name another workspace.
- Presentation code remains the only owner of docking objects and view models.
- SessionHost remains the authority and audit boundary.
- Provider prompt-cache prefixes are not invalidated by ordinary workspace
  topology or capability changes.
- Moving and resizing panels are intentionally absent until they have equally
  exact typed contracts and receipts.
