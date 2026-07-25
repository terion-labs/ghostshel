# GhostSHELL

GhostSHELL is a cross-platform terminal workspace for local and remote sessions. Its agent foundation is native .NET: the provider-neutral kernel, provider adapters, and governed runtime run in-process and add neither a Node.js sidecar nor software to remote machines. Anthropic, OpenAI, and OpenAI-compatible profiles use OS-vault credential references. The current production slice can inspect and operate exact terminal panels or fixed live tab/workspace/selected-terminal scopes through trusted screen, wait, text, paste, key, character-chord, mouse, interrupt, resize, workspace-graph, and bounded File Viewer tools; every mutation crosses the session-host capability broker, human-preemption or attachment barrier, policy, cancellation, and durable audit boundary. In the user's attached native-browser panel it can read state and perform governed navigation/stop. Snapshot, click, fill, and check contracts are implemented and tested through an explicit full-automation candidate profile, but remain disabled in production pending the native conformance and approval-context gates.

The repository contains a runnable vertical slice of the Pencil terminal-workspace design:

- durable workspaces, tabs, connections, screens, hosted terminal/File Viewer panels, live bounded local Statistics and Process Monitor panels, and metadata-only recent sessions;
- a docked governed-agent surface with provider and exact-target context, streaming, active-tool state, one-action approvals, cancellation, and an explicitly confirmed run-only YOLO window; the native kernel keeps model tool calls inert until the trusted runtime and session host authorize one typed action;
- native browser panels plus closed governed read-state, document-snapshot, click, fill, check, navigate, back, forward, reload, and stop contracts; production advertises state, guarded navigation, and stop, while snapshot/click/fill/check remain behind an explicitly injected full-automation candidate profile because their fixed scripts execute in a poisonable page realm; the candidate is exercised in tests but is not a named-platform conformance claim;
- a live local shell rendered by libghostty/Metal on macOS and a managed XTerm.NET surface backed by Porta.Pty on Windows and Linux;
- keyboard/IME, mouse, focus, resize, clipboard, terminal-screen reads, typed waits, and executable programmatic terminal input with physical-human preemption of agent input;
- deterministic domain, application, protocol, agent-kernel, terminal, provider, persistence, and architecture tests.

## Stack

- [.NET 10 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Avalonia 12](https://docs.avaloniaui.net/docs/get-started/)
- [libghostty](https://github.com/ghostty-org/ghostty) through an isolated native boundary
- [XTerm.NET](https://github.com/IvanJosipovic/XTerm.NET) and [Porta.Pty](https://github.com/IvanJosipovic/Porta.Pty) behind the same terminal contract on Windows and Linux

libghostty's embedding API is not versioned yet. GhostSHELL pins Ghostty `v1.3.1` at commit `332b2aefc6e72d363aa93ab6ecfc86eeeeb5ed28`, applies a small reviewed build patch, and hides the ABI behind a GhostSHELL-owned Objective-C shim. No Ghostty struct crosses into the core model or Avalonia UI.

## Run

Install the workspace-local .NET and Zig SDKs and build the pinned native runtime:

```sh
./scripts/bootstrap.sh
```

The first macOS bootstrap downloads and builds Ghostty and can take several minutes. Windows/Linux development, or a .NET-only macOS setup, can use `GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh`.

Build, test, and run:

```sh
./scripts/check.sh
./.dotnet/dotnet run --project src/GhostShell.Desktop/GhostShell.Desktop.csproj
```

Verify the real PTY, native text-input route, programmatic agent-actor input
plumbing, bundled terminfo, screen reads, and process exit. This engine smoke
tests the terminal boundary independently; governed provider-to-terminal
coverage lives in the managed runtime/broker/session-host suites:

```sh
./scripts/smoke-terminal.sh
```

Build a non-launching, unsigned macOS arm64 application-bundle candidate:

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output artifacts/macos-arm64-rc/GhostShell.app
```

The packager refuses incomplete native payloads, dependency-catalog drift,
tampered NuGet archives, and existing destinations. It emits deterministic
SPDX 2.3 evidence for the exact managed dependency closure plus the two
published Ghostty dylibs, then validates the exact bundle identity through the
acceptance fingerprint boundary. See the
[macOS packaging guide](./docs/macos-packaging.md) for guarantees and the
remaining native-license, signing, and notarization gates.

The desktop composition root selects the native libghostty engine/presentation on macOS and the portable PTY/state engine plus managed renderer on Windows and Linux. The repository gate is configured to build and test on macOS, Windows, and Linux and to publish self-contained Windows/Linux release candidates. Packaged interactive-TUI, IME, clipboard, mouse, resize, sleep/wake, and native PTY runs on named Windows/Linux systems remain a release acceptance requirement until their platform jobs have produced passing evidence. Use [`scripts/platform-terminal-acceptance.ps1`](./scripts/platform-terminal-acceptance.ps1) and the [platform acceptance guide](./docs/platform-terminal-acceptance.md) to capture host- and package-specific evidence without treating an unrun checklist as a pass.

Physical keyboard and screen-reader acceptance is likewise an explicit named-host gate. Use [`scripts/platform-accessibility-acceptance.ps1`](./scripts/platform-accessibility-acceptance.ps1) with the [VoiceOver, Narrator, and Orca acceptance guide](./docs/platform-accessibility-acceptance.md); the runner binds observations to one package and reader identity, retains stable descendant identities through cleanup, and emits a strict sanitized receipt.

OS-global Quick Terminal shortcuts use Carbon on macOS, `RegisterHotKey` on Windows, and `XGrabKey` in real X11 sessions. Wayland is reported as unsupported until a compositor-global portal backend can be implemented and verified safely.

Unsafe managed-terminal paste fails closed into an explicit in-terminal confirmation prompt. The Windows/Linux managed terminal supports bounded local scrollback, mouse selection and Ctrl+Shift+C copy, policy-gated clipboard gestures, and OSC 8 `http`/`https` activation through a revalidated confirmation prompt. Brokerless process-originated OSC 52 access fails closed.

## Solution

- `src/GhostShell.Core`: framework-independent IDs, definitions, invariants, and state machines.
- `src/GhostShell.Application`: typed application/session operations, results, lifecycle, capability, attachment, input-lease, and engine ports.
- `src/GhostShell.Protocol`: versioned transport envelopes and stream contracts.
- `src/GhostShell.Agent`: provider-neutral native conversation loop, strict stream reduction, inert tool proposals, bounded run events, cancellation fencing, and atomic compaction.
- `src/GhostShell.Agent.Providers`: native Anthropic and OpenAI-compatible model discovery and streaming adapters, exact-scope vault resolution, bounded HTTP/SSE parsing, and provider bindings for the governed runtime.
- `src/GhostShell.Agent.Runtime`: exact-target provider/tool orchestration that converts inert proposals into closed typed terminal or browser requests and returns bounded structured results.
- `src/GhostShell.SessionHost`: in-process runtime registry, ordered events, revisions, attachments, leases, browser-domain policy, and close policy.
- `src/GhostShell.Terminal`: libghostty and portable PTY/state adapters behind terminal attach/detach, resize, focus, typed input, screen-state, and lifecycle ports.
- `src/GhostShell.Monitoring`: package-free, cross-platform local resource sampling behind privacy-bounded statistics and process-session ports.
- `src/GhostShell.App`: Avalonia presentation based on `design/design.pen`; it depends only on application ports and Core projections.
- `src/GhostShell.Desktop`: executable composition root and platform adapter registrations.
- `tools/GhostShell.Packaging`: fail-closed release-candidate bundle assembly.
- `native/macos`: the small NSView/input/clipboard shim around pinned libghostty.
- `tests/*`: focused domain, application, protocol, terminal, session-host, provider, persistence, desktop, and architecture suites.

See [architecture.md](./docs/architecture.md) for boundaries and the implementation sequence.
