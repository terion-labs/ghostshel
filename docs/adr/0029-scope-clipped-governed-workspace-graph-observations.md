# ADR 0029: Scope-clipped governed workspace-graph observations

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0016](0016-host-owned-runtime-workspace-graph.md),
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

The built-in agent needs to understand the tabs and panels that the user has
already placed inside a run's target. Giving it an ambient workspace browser
would widen exact panel, connection-session, tab, and selected-panel scopes,
leak sibling topology, and turn provider-chosen identifiers into discovery
authority.

The host graph also changes for two different reasons. Membership, order,
ownership, and panel kind are authorization-relevant structure. Titles, focus,
visibility, lifecycle, graph revision, and sequence are observations that can
refresh without changing which objects the run owns. Treating both classes as
one fingerprint would either accept structural drift or unnecessarily stop a
run for a presentation-only refresh.

## Decision

GhostSHELL exposes four closed read-only tools through the native governed
runtime:

- `workspace.list`;
- `workspace.inspect`;
- `tab.list`;
- `panel.list`.

They are `Search` observations. Each proposal passes through the capability
broker, receives one exact authorization, is consumed once by SessionHost, and
has a complete durable audit outcome. The provider adapter has no graph client
and the agent runtime cannot call the host without this path.

### Scope clipping

The host resolves the original immutable `AgentTarget` and projects only graph
objects already inside it. A panel or current connection-session scope contains
one exact current graph panel. An open-tab scope contains that tab's current
panels. A workspace scope contains that one workspace. A selected-panel scope
contains only the exact selected set. `workspace.list` therefore returns the
workspace shell already implied by the target; it is not ambient workspace
discovery.

Graph observations include registered Terminal, Browser, File Viewer,
Statistics, and Process Monitor panels. They require every in-scope panel to be
registered in one consistent graph snapshot. A graphless Quick Terminal or
other graphless connection session advertises none of these tools.

### Model-controlled input

`workspace.list` and `workspace.inspect` accept only `{}`. `tab.list` and
`panel.list` accept an optional `offset` from the fixed set `0`, `16`, `32`,
and `48`; omission means zero and the page size is always 16. Duplicate,
unknown, fractional, negative, and out-of-range fields fail before
authorization.

The model cannot provide a window, workspace, tab, panel, session, query,
filter, sort, total, page size, continuation token, or projection field. A page
receipt reports its fixed offset and page size, returned in-scope item count,
bounded next offset, completion flag, and items. It exposes no total or
out-of-scope count from which sibling topology could be inferred.

### Structural binding and refresh

Preparation binds the run target to the ordered, scope-relative sequence of
`window/workspace/tab/panel/kind` identities. SessionHost reconstructs the same
clipped graph while holding its graph gate, consumes the one-action permit, and
compares the fresh structural binding before projection. In-scope addition,
removal, reordering, ownership change, or kind change fails closed as
`target_changed`.

Raw global tab and panel ordinals are deliberately absent from this binding.
Adding or reordering a sibling outside an exact or otherwise clipped scope
neither invalidates the action nor leaks that sibling. A connection-session
target additionally requires that the panel still owns that exact current
session, so supersession cannot reveal the replacement panel shell.
Workspace revision and graph sequence are omitted from every clipped provider
projection because they are global clocks that would otherwise disclose
unrelated sibling activity. They are returned only when the run target is the
complete workspace.

The governed runtime pins this structural sequence independently from its
operational session binding. Title, focus, visibility, lifecycle, workspace
revision, and graph-sequence refreshes are allowed. Graph observation can
therefore continue while an otherwise unchanged session moves from `Active` to
`Starting` or `Closing`; terminal, browser, and File Viewer operations still
require their exact usable operational binding and fail closed.

### Bounded hostile metadata

Results contain only host-owned IDs, panel kind, focus/visibility, bounded
titles, and—for a complete workspace target only—workspace revision and graph
sequence. They contain no session ID, connection, capability, address, current
directory, browser, File Viewer, process, terminal content, or reusable permit
data.

Titles are untrusted. Secret-shaped or unsafe-Unicode titles are replaced
before continuation; accepted text is valid Unicode and is truncated
rune-safely to 128 UTF-8 bytes with explicit redaction/truncation metadata.
Every result carries
`content_origin=untrusted_workspace_graph_metadata`, its exact scope kind, and
whether that scope is clipped. The application projection and the actual
serialized JSON are each limited to 64 KiB. Raw titles and graph content are
excluded from durable audit.

## Consequences

- The agent can reason about the user's current layout, including non-session
  panels, without acquiring ambient workspace discovery.
- Exact and clipped scopes remain stable across unrelated sibling changes and
  presentation-only refreshes.
- Structural drift, session supersession, missing graph registration,
  malformed input, and oversized or unsafe output fail closed.
- Fixed paging covers at most the first 64 tabs or panels. Search, filters,
  arbitrary continuation, cross-window discovery, and graph mutation remain
  outside this observation slice.

## Alternatives rejected

- Returning the complete desktop graph would widen every target below the
  window and disclose unrelated workspaces, tabs, and panels.
- Accepting provider-supplied IDs or filters would turn untrusted data into
  discovery authority.
- Binding global ordinals would make out-of-scope sibling changes observable
  through action failure.
- Binding revision, sequence, titles, focus, visibility, or lifecycle as
  structure would make harmless refreshes terminate otherwise valid runs.
- Omitting one-action authorization because the tools are read-only would
  bypass policy, revocation, and durable evidence for information disclosure.
