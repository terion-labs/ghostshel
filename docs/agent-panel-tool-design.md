# Agent panel tool design

- Status: Proposed
- Date: 2026-08-14
- Scope: the built-in native agent and the seven user-placeable panel kinds
- Builds on: [ADR 0019](adr/0019-one-action-agent-capability-broker.md),
  [ADR 0040](adr/0040-cross-platform-libghostty-vt-terminal.md), and
  [ADR 0042](adr/0042-cef-off-screen-browser-runtime.md)
- Security basis:
  [Agent-to-tool threat model](security/agent-tool-threat-model.md)

## Executive decision

GhostSHELL should expose a capability-negotiated tool contribution for each
hosted panel session. Tools operate the panel's typed engine boundary, not its
Avalonia view model and not the desktop's global pointer or keyboard.

The normal interaction loop is:

1. discover an exact panel from the scope-clipped workspace graph;
2. observe fresh panel state and receive a revision plus opaque references;
3. perform one bounded action against that exact revision/reference;
4. wait for a deterministic state transition when necessary; and
5. observe again to verify the result.

Terminal and browser tools need two levels:

- a normal semantic level optimized for reliable agent use; and
- a low-level input level for TUIs, canvas applications, inaccessible web
  controls, and debugging.

Browser scripting and raw DevTools access form a third, explicitly privileged
level. They are not substitutes for semantic tools and must not be silently
enabled by ordinary browser interaction permission.

Database and Docker panels are not currently hosted `IPanelSession` instances.
They must first move behind SessionHost-owned typed sessions. Directly wiring
agent tools to their presentation view models would bypass target binding,
one-action authorization, cancellation, audit, and outcome-uncertainty rules.

## Research and current implementation

### External interaction model

Browserbase's current Browse CLI exposes navigation, accessibility snapshots,
reference-based element actions, typing, uploads, screenshots, waits, viewport
control, getters, predicates, JavaScript evaluation, raw CDP, tabs, network
capture, and coordinate mouse input. Its recommended workflow is
`open -> snapshot -> act by ref -> snapshot`, and its references are refreshed
when page state changes:

- [Browse CLI documentation](https://docs.browserbase.com/integrations/skills/browse-cli)
- [Browse CLI command reference](https://github.com/browserbase/stagehand/tree/main/packages/cli)
- [Browserbase agent skills](https://github.com/browserbase/skills)

The vendored cmux reference independently reaches the same conclusion for an
embedded browser: P0 includes snapshot, evaluation, waits, semantic actions,
keyboard, scrolling, getters, predicates, and screenshots; network, emulation,
and raw input are power features. See
[`references/cmux/docs/agent-browser-port-spec.md`](../references/cmux/docs/agent-browser-port-spec.md).

The useful lesson is the workflow and command coverage, not its trust model.
GhostSHELL must continue to use exact hosted sessions, trusted risk labels,
one-action authorizations, origin containment, bounded results, and no
transparent mutation retry.

### CEF feasibility

The vendored Exclr8CEF revision already exposes the required primitives:

| Agent need | Existing CEF boundary |
| --- | --- |
| Accessibility snapshot | `CefBrowser.Accessibility.GetFullTreeAsync` |
| DOM identity and geometry | `Dom.DescribeNodeAsync`, `GetBoxModelAsync`, `GetContentQuadsAsync` |
| Scroll/focus element | `Dom.ScrollIntoViewAsync`, `Dom.FocusAsync` |
| Screenshot | `Page.CaptureScreenshotAsync` / `CapturePageAsync` |
| Exact text insertion | `Input.InsertTextAsync` |
| Gesture scroll/tap | `Input.SynthesizeScrollGestureAsync`, `SynthesizeTapGestureAsync` |
| Raw mouse | `SendMouseMove`, `SendMouseClick`, `SendMouseWheel` |
| Raw keyboard | `SendKeyEvent` |
| Page evaluation | `EvaluateJavaScriptAsync` or CDP `Runtime.evaluate` |
| Typed CDP | `ExecuteDevToolsMethodAsync` and domain clients |
| Network events/body | `CefBrowser.Network` |
| Human-visible highlight | `CefBrowser.Overlay` |
| OOPIF/worker discovery | `CefBrowser.Target` |
| File chooser/download | `FileDialog` and download callbacks |

These are present under `vendor/exclr8cef/src/Exclr8Cef`. The current
`CefBrowserView` deliberately returns unavailable/unknown results for semantic
automation, as required by ADR 0042. The new browser automation adapter should
replace that fail-closed stub; it should not add JavaScript strings to
Application or SessionHost contracts.

### Current panel inventory

`PanelKind.Placeholder` is a layout affordance, not an operational panel, and
receives no tools.

| Panel | Hosted session now | Current agent tools | Main gap |
| --- | --- | --- | --- |
| Terminal | Yes | read, text, paste, key, chord, mouse, wait, interrupt, resize | scrollback read/search and explicit viewport scrolling |
| Browser | Yes | state, candidate snapshot/click/fill/check, navigation | production semantic adapter and the rest of the interaction surface |
| File Viewer | Yes | list, stat, bounded text read, mkdir, delete | rename, search, transfers, ACLs, non-text artifacts |
| Statistics | Yes | one bounded read | no essential gap |
| Process Monitor | Yes | bounded sorted list | optional exact-row refresh; no control tool is justified |
| Database Viewer | No | none | hosted relational/Redis session and complete policy boundary |
| Docker | Only embedded terminal/file children are hosted | none | hosted Docker session and typed Docker tools |

## Cross-panel contract

### Targeting

- Exact panel/session targets omit `panel_id`.
- Workspace or tab scopes require one `panel_id` enumerated from the fresh
  capability-specific eligible set, even when only one panel is eligible.
- Every prepared action narrows to one exact panel session before approval.
- Cross-panel actions, such as a file transfer or browser upload, bind both
  exact sessions and both current revisions in one authorization.
- Presentation labels are untrusted descriptions and never provide authority.

### Capability negotiation

A tool is advertised only when all of the following are true:

1. the panel is the current active graph-owned session;
2. the session advertises the exact operation capability;
3. a live renderer/attachment exists when the operation needs one;
4. the runtime has the required host-side broker/composer contribution; and
5. the operation can uphold human-preemption and outcome semantics.

Tool definitions are rebuilt after every provider tool-result round, matching
the existing contribution architecture in
`GovernedAgentRuntime.ToolContributions.cs`.

### Revisions, references, and receipts

Observation results use three distinct clocks where applicable:

- `session_revision`: hosted lifecycle/capability state;
- `content_revision`: terminal grid, file listing, monitor sample, or database
  result revision; and
- `document_revision`: committed top-level browser document.

Opaque references are host-generated capabilities to address an observed
object, not selectors or authority on their own. Each reference is bound to
the panel/session, relevant revision, snapshot nonce, object identity, and an
expiry. The host revalidates the underlying object immediately before action.

Mutations return a typed commit receipt. Once an operation crosses its commit
point, cancellation cannot turn it into a safe retry. A missing or invalid
post-commit result is `*_outcome_unknown`, stops provider continuation, revokes
run authority, and requires human inspection, following the existing browser
and file mutation rules.

### Result bounds

- Ordinary JSON tool results remain at or below 64 KiB serialized.
- Text fields use strict Unicode and rune-safe byte limits.
- Rows, nodes, cells, log lines, network records, and matches have independent
  count and byte limits plus explicit `truncated` state.
- Images, downloads, uploads, exports, and large response bodies use an opaque,
  run-scoped artifact broker. Tools never accept or return an ambient local
  filesystem path.
- All terminal, browser, file, database, Docker, and process content is marked
  untrusted in provider results.

### Waits instead of polling

Long-lived interactive panels should expose bounded waits. A wait has one
condition, a maximum 30-second deadline, cancellation, and a final fresh
snapshot. It does not synthesize input and never retries another action.

### Input ownership

Terminal already has a human-preemptible agent input barrier. Browser should
gain the equivalent `browser.agent_input_barrier` and one-action input lease.
Physical user input advances the input epoch and preempts an agent lease. Raw
input is panel-relative only; neither terminal nor browser tools may move the
desktop cursor, type into another application, or address screen coordinates.

## Common panel tools

Keep the existing graph tools for every hosted operational panel:

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `workspace.list` | List the scope-visible workspaces needed to resolve a broad target | `Search` / Observation |
| `workspace.inspect` | Read one scope-visible workspace's fresh graph metadata | `Search` / Observation |
| `tab.list` | List scope-visible tabs and their graph identities | `Search` / Observation |
| `panel.list` | List scope-visible panels, kinds, and hosted-state summaries | `Search` / Observation |
| `panel.inspect` | Fresh identity, lifecycle, health, focus, visibility, activity, and supported operations | `Search` / Observation |
| `panel.focus` | Activate the exact containing tab and panel | `RunCommands` / Routine |

Creating, closing, splitting, moving, or resizing layout panels is a separate
workspace-editing capability and is outside this panel-operation design.

## Terminal panel

### Design decision

Do not add a subprocess-style `terminal.exec` abstraction. The product promise
is control of the exact interactive PTY the user sees, including shells, REPLs,
full-screen TUIs, prompts, remote hosts, and container terminals. Command exit
codes are available only when shell integration actually observed them.

Prefer text/state operations for normal shells and semantic shell events for
completion. Use key and mouse input only for interactive applications. The
agent reads terminal modes before deciding whether a wheel belongs to hosted
scrollback or to the remote TUI.

### Tool set

| Tool | Arguments and result | Capability / risk | Status |
| --- | --- | --- | --- |
| `terminal.read_screen` | No required args. Returns bounded visible rows, cursor, dimensions, title, cwd, alternate-screen, paste/mouse modes, scrollback counts, content revision, and recent OSC 133 boundaries/events. | `TerminalRead` / Observation | Keep |
| `terminal.read_scrollback` | `anchor: top|bottom|before|after`, optional opaque row anchor, `max_lines` (16/64/200). Non-mutating bounded history read. | `TerminalRead` / Observation | Add after native projection |
| `terminal.find` | Exact literal text, direction, maximum matches. Returns bounded excerpts and opaque match anchors without moving the user's viewport. | `TerminalRead` / Observation | Add; native full-scrollback search already exists |
| `terminal.scroll_viewport` | `direction: up|down|top|bottom`, `unit: line|page`, bounded `amount`. Reject on alternate screen when there is no hosted scrollback. Returns the resulting viewport snapshot. | `RunCommands` / Routine | Add; typed port exists |
| `terminal.send_text` | Exact printable text, no Enter, max 2,048 UTF-8 bytes. | `RunCommands` / Mutation | Keep |
| `terminal.paste` | Exact bounded text using bracketed-paste semantics; controls escaped in approval. | `RunCommands` / Mutation, human/confirmed policy only | Keep |
| `terminal.send_keys` | One named special key plus known modifiers. Extend the enum only as libghostty-vt proves encoding. | `RunCommands` / Mutation | Keep |
| `terminal.send_chord` | One lowercase ASCII letter with exactly Control or Alt. | `DestructiveTerminalActions` / Destructive | Keep |
| `terminal.send_mouse` | One zero-based cell move/down/up/drag/wheel event, modifiers, and expected content revision. Valid only inside current dimensions and when terminal mouse tracking supports the event. | `RunCommands` / Mutation | Tighten existing contract |
| `terminal.wait` | One of text, newer revision, stable screen, prompt-ready, or command-finished; max 30 seconds. Returns a fresh screen. | `TerminalRead` / Routine | Extend |
| `terminal.interrupt` | One typed interrupt. | `DestructiveTerminalActions` / Destructive | Keep |
| `terminal.resize` | Exact bounded cell dimensions, preserving attachment-owned scale. | `RunCommands` / Mutation | Keep |

Selection read/write and clear-scrollback are not needed for reliable agent
operation. They should remain human UI features until a concrete agent workflow
justifies their privacy and destructive semantics.

### Terminal-specific rules

- `read_screen` never auto-scrolls.
- `scroll_viewport` manipulates local hosted history; wheel events sent to a TUI
  remain `send_mouse` mutations. The host never guesses between those effects.
- Mouse coordinates are checked against the same screen revision and grid used
  by the agent. Resize or content-mode drift fails stale.
- `wait(prompt-ready)` is offered only when shell integration is active. No
  prompt regex heuristic becomes authority.
- Every input tool requires `terminal.agent_input_barrier`; human input wins.
- No text/chord/paste input is automatically retried after PTY dispatch.

## Browser panel

### Architecture

Add a private CEF automation adapter behind `IEmbeddedBrowserView`. Public
Application contracts remain typed and engine-neutral. The adapter uses CEF's
CDP domain clients and OSR input APIs; it does not expose `CefBrowser`, backend
node IDs, JavaScript object IDs, or raw JSON outside `GhostShell.Browser`.

The semantic snapshot is built primarily from Chromium's Accessibility tree.
Interactive nodes receive random opaque refs. Internally a lease may contain
the main-frame/document identity, frame ID, backend DOM node ID, accessibility
role/state, snapshot nonce, and geometry evidence. Node IDs are never returned
to the provider.

A reference expires on top-level navigation, renderer replacement, next
snapshot, completed mutation, or a short time limit. Before using it, the host
re-resolves the backend node and rechecks frame, role, visibility, enabled/edit
state, and origin. A replaced node is stale even if a selector would find a
similar replacement.

Element clicks use real CEF/CDP input at a revalidated visible point, not a page
realm `.click()` call. Typing uses focus plus `Input.insertText`/key dispatch so
framework event handlers observe normal input. Fixed semantic actions use an
isolated world only where DOM property access is unavoidable.

### Normal observation and navigation tools

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.read_state` | URL, origin, title, load state, history flags, focused state, viewport CSS size/scale, document revision, active downloads, and input epoch. | `BrowserData` / Observation |
| `browser.snapshot` | `interactive_only`, optional text filter and `max_depth`; returns a bounded accessibility tree and opaque refs. | `BrowserData` / Observation |
| `browser.screenshot` | `viewport|full_page`, optional bounded clip, PNG/JPEG/WebP quality; returns an image attachment/artifact and the exact document/viewport revision. | `BrowserData` / Observation |
| `browser.get` | Ref plus `text|value|html|attribute|box|styles|accessible_name`. Attribute/style names use bounded allowlists. | `BrowserData` / Observation |
| `browser.is` | Ref plus `visible|enabled|checked|selected|editable|focused`. | `BrowserData` / Observation |
| `browser.wait` | One of load state, URL pattern, text, ref state, document revision, or network idle; max 30 seconds. | `BrowserData` / Routine |
| `browser.navigate` | Absolute HTTP(S) URL or `about:blank`, with current origin/start revision bound into approval. | `BrowserNavigation` / Mutation |
| `browser.back` / `browser.forward` / `browser.reload` / `browser.stop` | No ambient target args. Return final state or a typed no-op. | `BrowserNavigation` / Mutation |

### Normal semantic interaction tools

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.click` | Ref, `button`, click count, modifiers. Real input dispatch at a revalidated point. | `BrowserInteraction` / Mutation |
| `browser.fill` | Ref and replacement text; empty text clears. Restricted to fillable controls and verifies final value. | `BrowserInteraction` / Mutation |
| `browser.type` | Optional ref, text, optional bounded per-character delay. Focuses and appends through input semantics. | `BrowserInteraction` / Mutation |
| `browser.check` | Ref and desired `checked: true|false`; idempotent final-state semantics. | `BrowserInteraction` / Mutation |
| `browser.select` | Select ref plus one or more exact option values/labels from a fresh snapshot. Returns selected values. | `BrowserInteraction` / Mutation |
| `browser.hover` | Ref; moves the panel-local pointer and returns resulting cursor/hover state. | `BrowserInteraction` / Mutation |
| `browser.focus` | Ref; focuses a page control without moving desktop focus elsewhere. | `BrowserInteraction` / Routine |
| `browser.scroll_into_view` | Ref and alignment. Returns fresh geometry. | `BrowserInteraction` / Routine |
| `browser.highlight` | Ref, bounded duration. Uses CDP Overlay for human-visible action preview. | `BrowserInteraction` / Routine |

### Low-level input tools

These are necessary for canvas applications, custom editors, drag surfaces,
games, remote consoles, and pages with incomplete accessibility trees. They
remain panel-local and require the browser input barrier.

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.mouse` | `move|down|up|click|wheel`, viewport-relative CSS `x/y`, button, click count, wheel deltas, modifiers. Coordinates bind to viewport and document revision. | `BrowserInteraction` / Mutation |
| `browser.key` | `press|down|up`, normalized key/code, known modifiers, repeat flag. Text insertion remains `type`/`fill`. | `BrowserInteraction` / Mutation |
| `browser.drag` | Source ref or point, destination ref or point, button/modifiers, bounded steps. One host-owned gesture with capture-loss handling. | `BrowserInteraction` / Mutation |
| `browser.scroll` | CSS-pixel deltas and optional origin point; uses wheel or synthesized gesture and returns resulting viewport state. | `BrowserInteraction` / Mutation |

### Script and DevTools power tier

| Tool | Contract | Capability / risk |
| --- | --- | --- |
| `browser.evaluate` | Bounded JavaScript source, `world: isolated|main`, optional await, 5-second default/30-second max. Returns JSON-serializable data only, max 64 KiB; no object handles. It binds exact origin/document revision. Main-world use is always explicitly approved. | new `BrowserScripting` / Privileged |
| `browser.cdp` | One method plus bounded params and timeout. The production model-facing allowlist is versioned per CEF build. Prefer typed tools for Input, navigation, files, permissions, downloads, and target lifecycle. No arbitrary raw CDP stream is enabled by default. | new `BrowserDiagnostics` / Privileged |
| `browser.console_read` | Read bounded console and exception buffers with timestamps and source URLs. | `BrowserDiagnostics` / Observation |
| `browser.console_clear` | Clear this panel's host-side diagnostic buffer; it does not run page code. | `BrowserDiagnostics` / Mutation |
| `browser.network_records` | Read bounded, redacted request metadata from an existing capture. Authorization, cookies, Set-Cookie, proxy credentials, and secret-shaped values are removed. | `BrowserDiagnostics` / Observation |
| `browser.network_capture` | Start, stop, or clear the exact panel's bounded diagnostic capture. | `BrowserDiagnostics` / Privileged |
| `browser.network_body` | Read one exact request ref's redacted, size-limited, same-origin response body. | `BrowserDiagnostics` / Privileged |

`browser.cdp` is deliberately not “any method because CEF accepts it.” The
allowlist excludes `Browser.close`, arbitrary `Target.*` attachment/control,
security bypass, permission grants, download-path changes, local-file access,
cookie/auth extraction, and unbounded tracing. A future explicit developer mode
may expose a wider list, but only to exact-panel runs with a human approval per
call; never through `Auto` or YOLO.

Both script worlds and every allowed CDP method run under the same frozen-origin
navigation guard as normal browser interaction. A script-initiated top-level
navigation cannot bypass the approved origin simply because the script itself
was approved.

### Uploads and downloads

| Tool | Contract | Capability / risk |
| --- | --- | --- |
| `browser.upload` | Element ref plus one or more run-scoped artifact refs; never an arbitrary path. The broker validates size, regular-file identity, MIME/name metadata, lifetime, and user authorization, then resolves the CEF file dialog once. | `ArtifactTransfer` / Privileged |
| `browser.downloads` | Read bounded progress and metadata for this panel's downloads. | `BrowserData` / Observation |
| `browser.download_cancel` | Cancel one exact pending download. | `ArtifactTransfer` / Mutation |

Downloads are denied unless an action has an approved download destination
policy. Accepted bytes enter an owner-only run artifact. The result exposes a
sanitized name, media type, size, hash, and opaque artifact ref. Exporting to a
File Viewer is a separately authorized cross-panel file copy or move.

### Browser-specific rules

- Snapshot and page content are untrusted; instructions found in a page are
  never tool authority.
- Standard navigation and interaction preserve the accepted frozen-origin
  policy. Cross-origin transitions require a new approved navigation action.
- Script/CDP tools cannot smuggle credentials through source, params, URLs, or
  results. Secret use requires the existing secret broker and a purpose-built
  flow; secrets never enter model-visible arguments.
- Input dispatch is the commit point. Post-dispatch ambiguity is never retried.
- Popups map to a future graph-owned Browser panel creation request; they do
  not silently create an untracked CEF target.
- GhostSHELL Browser panels are already the product's browser tabs. CEF target
  management must not invent a second hidden tab model. Use workspace/panel
  tools for tab topology.

## File Viewer panel

Keep structured path segments relative to the session's trusted root. Do not
replace them with caller-supplied native paths or provider URLs.

| Tool | Purpose | Capability / risk | Priority |
| --- | --- | --- | --- |
| `files.list` | Bounded directory page with structured child paths and continuation | `ReadFiles` / Observation | Existing |
| `files.stat` | Exact bounded metadata | `ReadFiles` / Observation | Existing |
| `files.read` | Bounded UTF-8 preview | `ReadFiles` / Observation | Existing |
| `files.search` | Provider-capability-gated bounded name/content search under one directory | `ReadFiles` / Observation | P1 |
| `files.mkdir` | One non-root directory, `MustNotExist` | `EditFiles` / Mutation | Existing |
| `files.rename` | Exact source and new sibling name with version/precondition from a fresh stat | `EditFiles` / Mutation | P0 after a separate `GovernedRename` provider capability |
| `files.delete` | One exact file or empty directory, non-recursive | `EditFiles` / Destructive | Existing, capability gated |
| `files.copy` | Copy one or more exact entries to an exact hosted destination panel/directory with explicit conflict policy | `EditFiles` / Mutation | P1 after a separate governed copy capability |
| `files.move` | Move one or more exact entries to an exact hosted destination panel/directory with explicit conflict policy | `EditFiles` / Destructive | P1 after a separate governed move capability |
| `files.transfers` | List bounded session-owned transfer status | `ReadFiles` / Observation | P1 |
| `files.transfer_cancel` / `retry` | One exact transfer; retry only a provider-declared safely retryable, uncommitted transfer | `EditFiles` / Mutation | P1 |
| `files.access_read` | Bounded POSIX mode or provider ACL | `ReadFiles` / Observation | P2 |
| `files.access_set` | Exact version-bound mode/grant replacement | `EditFiles` / Privileged | P2 |

Non-text previews return metadata plus an artifact/image attachment when the
provider and model support it. `files.read` must not inline arbitrary binary,
PDF, database, or image bytes into tool JSON.

Cross-panel transfer binds source and destination provider generations,
locations, versions, capabilities, and sessions. A move is not reported as a
single atomic effect when the provider implements copy then delete; partial
transfer remains explicit and non-retryable without inspection.

Ordinary provider `Rename`, `Copy`, and `Move` flags are human-UI capabilities,
not proof of race-safe governed semantics. Each agent mutation needs a separate
production-assigned governed capability, following the existing mkdir/delete
model.

## Statistics panel

The existing `statistics.read` is the correct complete tool set for this panel.
It returns one fresh bounded numeric host snapshot: uptime, logical processors,
observed process counts, aggregate observed CPU/working set, and network rates.

Do not add a generic sampler or arbitrary interval loop. The agent can call the
bounded read again after a user-visible progress update when comparison is
actually needed. Future disk/GPU/temperature metrics should extend the typed
snapshot and capability rather than create raw system APIs.

The current `ProcessControl` capability name is too broad for read-only system
statistics. New policy storage should use a separate `SystemData` capability;
old policy payloads fail closed until migrated explicitly.

## Process Monitor panel

Keep `processes.list` as the primary and, for now, only tool:

- sort is one of CPU descending, memory descending, name ascending, or PID;
- limit remains one of 16, 32, or 64;
- results exclude command line, executable path, user, environment, open files,
  and terminal content; and
- the tool targets only the local hosted Process Monitor, never a remote shell.

Do not add terminate, signal, priority, attach, open-file, or environment tools
until the human panel supports them and a separate process-control design owns
identity reuse, privileges, confirmation, and outcome uncertainty.

Use a new `ProcessData` capability for listing. Reserve the existing
`ProcessControl` capability for future mutations rather than using a mutation
name for an observation.

## Database Viewer panel

### Required architecture first

Introduce `IDatabasePanelSession : IPanelSession` and a SessionHost factory.
The session immutably binds driver, connection definition/revision, selected
database, tunnel generation, and a secret reference. The session owns
connection cancellation and publishes only sanitized endpoint/session facts.
Presentation consumes that session rather than holding an unrestricted
`IDatabasePanelClient` plus connection string.

Redis should use a hosted Redis session under the same `PanelKind`, but advertise
Redis-specific capabilities and tools. Do not flatten Redis operations into SQL.

### Relational tools

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `database.read_state` | Driver, server/TLS facts, selected catalog/schema, readiness, capabilities; no connection string/password | new `DatabaseRead` / Observation |
| `database.list_objects` | Bounded tables/views/routines with opaque object refs | `DatabaseRead` / Observation |
| `database.describe_object` | Columns, keys, nullability, types, indexes/relations where available | `DatabaseRead` / Observation |
| `database.read_table` | Structured filters/sorts/page against one object ref; bounded rows/cells/bytes | `DatabaseRead` / Observation |
| `database.schema_graph` | Bounded table/foreign-key graph, optionally clipped to named objects | `DatabaseRead` / Observation |
| `database.query_read` | One bounded SQL statement under an enforced read-only connection/transaction and read-only principal | `DatabaseRead` / Observation |
| `database.execute` | One exact SQL statement or structured table change; SQL hash and sanitized preview bound into approval | new `DatabaseWrite` / Privileged |

SQL text parsing is not a security boundary. `query_read` is available only
when the driver can enforce read-only behavior at the server/transaction level;
otherwise the tool is absent. Stored procedures, multi-statements, DDL, session
settings, and provider-specific escape commands are never accepted by the read
tool merely because a parser labels them SELECT-like.

`database.execute` is never automatically retried. Rows affected and committed
transaction identity form the receipt. Disconnect, timeout, or malformed output
after dispatch is `database_outcome_unknown`.

### Redis tools

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `redis.scan` | Pattern plus opaque cursor and bounded count | `DatabaseRead` / Observation |
| `redis.read` | Exact opaque key ref, type, TTL, size, and bounded entries | `DatabaseRead` / Observation |
| `redis.search` | Exact index and bounded query/results when supported | `DatabaseRead` / Observation |
| `redis.set` | Type-specific exact set/append/update with TTL/precondition where supported | `DatabaseWrite` / Mutation |
| `redis.expire` | Exact key and desired TTL/persist state | `DatabaseWrite` / Mutation |
| `redis.delete` | Exact key or collection entry | `DatabaseWrite` / Destructive |
| `redis.publish` | Exact channel and bounded payload | `DatabaseWrite` / Mutation |

Subscriptions are a human live-view feature, not a good finite agent tool.
When an agent needs messages, add a bounded `redis.read_messages` observation
over a separately established subscription rather than leaving a provider tool
call open indefinitely.

## Docker panel

### Required architecture first

Introduce `IDockerPanelSession : IPanelSession`. It immutably binds the exact
local/SSH connection definition and Docker engine generation, owns refresh/log
operations, and exposes typed operations currently reached through
`IDockerEngineClient`. The main Docker panel, its embedded container terminal,
and its embedded file session keep distinct session identities and roles in the
workspace graph.

### Tool set

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `docker.read_state` | Engine facts and bounded container/image/volume/network summaries | new `DockerData` / Observation |
| `docker.inspect` | Exact resource ref with bounded normalized properties; raw JSON optional and bounded | `DockerData` / Observation |
| `docker.logs` | Exact container ref, bounded lines, before/since cursor, text filter/context | `DockerData` / Observation |
| `docker.files_list` / `docker.files_stat` / `docker.file_read` | Bounded container/image/volume file observation | `DockerData` / Observation |
| `docker.container_start` | Start one exact stopped container | existing `Docker` / Mutation |
| `docker.container_stop` | Stop one exact running/paused container | `Docker` / Destructive |
| `docker.container_restart` | Restart one exact container | `Docker` / Destructive |
| `docker.container_pause` / `resume` | Change one exact container run state | `Docker` / Mutation |
| `docker.open_shell` | Resolve a reviewed shell path and create/focus one embedded terminal session; subsequent operation uses terminal tools | `Docker` / Mutation |

Do not expose a generic `docker exec <string>` tool. An interactive shell is a
real hosted Terminal panel and receives the terminal tool set, input lease,
screen state, waits, and audit behavior. Do not expose the current multi-container
stack loop initially: partial success across independently committed container
actions needs a separate batch receipt design.

Removal/prune/build/pull/push/copy-in/copy-out are absent until the human panel
and typed Docker client support them with bounded progress and exact receipts.

## Policy capability changes

Append, never reorder, the following capabilities in `AgentCapability`:

| Capability | Purpose | Default |
| --- | --- | --- |
| `BrowserScripting` | JavaScript evaluation in an exact browser document | Off |
| `BrowserDiagnostics` | console/network/CDP diagnostics | Off |
| `DatabaseRead` | bounded relational and Redis observations | Off |
| `DatabaseWrite` | SQL/row/Redis mutations | Off |
| `DockerData` | Docker observations distinct from lifecycle control | Off |
| `SystemData` | aggregate local statistics | Off |
| `ProcessData` | bounded local process listing | Off |
| `ArtifactTransfer` | browser upload/download and cross-panel artifact movement | Off |

Existing durable payload shapes remain readable but every absent new capability
fails closed. A deliberate migration/UI edit is required to enable one. Keep
`Docker` for lifecycle mutation and `ProcessControl` for any future process
mutation; do not overload observation and control under one permission.

## Implementation sequence

### Phase 0: contract foundation

1. Add the new policy capabilities with fail-closed schema compatibility.
2. Define the run-scoped artifact broker and common bounded result envelope.
3. Add `browser.agent_input_barrier` and browser input-lease/epoch support.
4. Define hosted Database and Docker session contracts before any tools for
   those panels.

### Phase 1: terminal completion

1. Add non-mutating bounded scrollback projection.
2. Wire native full-scrollback search.
3. Expose hosted viewport scrolling and prompt/command wait conditions.
4. Add parser/composer/host/result tests following existing terminal patterns.

This phase is low architectural risk because the typed terminal ports and
libghostty-vt state already exist.

### Phase 2: CEF semantic browser

1. Implement CEF accessibility snapshot and opaque backend-node leases.
2. Add get/is/wait, screenshot, and element geometry/focus.
3. Implement click/fill/type/check/select with real input and exact post-action
   verification.
4. Replace the production fail-closed automation profile only after hostile-page,
   navigation-order, mutation, cancellation, and recovery conformance passes on
   every supported CEF RID.

### Phase 3: CEF low-level and power tier

1. Add raw panel-relative mouse/key/scroll/drag.
2. Add the bounded script runner with isolated/main-world distinction.
3. Add redacted console/network buffers.
4. Add the versioned CDP method allowlist.
5. Add artifact-backed upload and download.

### Phase 4: hosted Database and Docker

1. Move presentation onto hosted sessions without changing human behavior.
2. Add read-only tools and conformance first.
3. Add one exact mutation at a time with commit and outcome-unknown tests.

### Phase 5: File Viewer completion

1. Add rename.
2. Add transfer observation/cancel/retry.
3. Add exact cross-panel copy, then move after partial-effect evidence.
4. Add ACL tools only for providers with race-safe version semantics.

## Verification gates

Every tool family needs:

- schema tests for exact and broad scope;
- capability advertisement/removal tests;
- parser rejection of duplicate/unknown/oversized fields;
- composer digest and approval-presentation tests;
- host target/revision drift tests adjacent to permit consumption;
- cancellation before and after commit;
- human-input preemption for terminal/browser input;
- bounded-output and hostile-Unicode/content tests;
- no-retry tests after disconnect/timeout/invalid receipt;
- renderer/session replacement and close races; and
- provider continuation tests for success, ordinary failure, and
  outcome-unknown quarantine.

Browser additionally needs deterministic local fixtures for accessibility,
shadow DOM, iframes/OOPIF, canvas, contenteditable, file input, downloads,
redirects, SPA navigation, dialogs, renderer crash, prototype poisoning, and
input ordering. Public websites are not conformance fixtures.

Database mutation tests use disposable databases and induced post-dispatch
disconnects. Docker tests use disposable containers and prove exact resource
identity across refresh. File transfer tests cover provider-generation drift
and partial move. Terminal tests replay recorded VT fixtures for shells, REPLs,
alternate-screen TUIs, mouse tracking, bracketed paste, and OSC 133 boundaries.

## Rejected alternatives

- **One generic `panel.invoke` tool.** It destroys capability-specific schemas,
  risk classification, least-privilege advertisement, and provider clarity.
- **UI automation against Avalonia controls.** It races presentation state and
  bypasses the engine/session authority that the user actually cares about.
- **Desktop-wide mouse and keyboard tools.** They can escape the target panel
  and operate other applications.
- **Terminal command execution beside the PTY.** It would not operate the
  interactive session the user sees and would diverge on SSH, Docker, WSL,
  REPLs, and TUIs.
- **Selectors as browser identity.** Selectors can retarget after DOM changes;
  opaque snapshot-bound backend-node leases fail stale instead.
- **Page-realm scripts for every browser action.** They are weaker against
  hostile prototype changes and generate less realistic input than CEF/CDP.
- **Unrestricted CDP by default.** It collapses navigation, data, interaction,
  filesystem, network-secret, and target-lifecycle permissions into one ambient
  escape hatch.
- **Agent tools on Database/Docker view models.** Presentation does not own
  authorization, exact target resolution, durable audit, or mutation receipts.
