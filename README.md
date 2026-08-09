# GhostSHELL

GhostSHELL is a cross-platform terminal workspace for local and remote sessions. Its agent foundation is native .NET: the provider-neutral kernel, provider adapters, and governed runtime run in-process and add neither a Node.js sidecar nor software to remote machines. Anthropic, OpenAI, and OpenAI-compatible profiles use OS-vault credential references. The current production slice can inspect and operate exact terminal panels or fixed live tab/workspace/selected-terminal scopes through trusted screen, wait, text, paste, key, character-chord, mouse, interrupt, resize, workspace-graph, and bounded File Viewer tools; every mutation crosses the session-host capability broker, human-preemption or attachment barrier, policy, cancellation, and durable audit boundary. In the user's attached native-browser panel it can read state and perform governed navigation/stop. Snapshot, click, fill, and check contracts are implemented and tested through an explicit full-automation candidate profile, but remain disabled in production pending the native conformance and approval-context gates.

The repository contains a runnable vertical slice of the Pencil terminal-workspace design:

- durable workspaces, tabs, connections, screens, hosted terminal/File Viewer panels, live bounded local Statistics and Process Monitor panels, and metadata-only recent sessions;
- a docked governed-agent surface with provider and exact-target context, streaming, active-tool state, one-action approvals, cancellation, and an explicitly confirmed run-only YOLO window; the native kernel keeps model tool calls inert until the trusted runtime and session host authorize one typed action;
- CEF off-screen browser panels composed as ordinary Avalonia content, with closed governed state/navigation contracts, fail-closed prompts and permissions, renderer-crash replacement, deterministic shutdown, and verified per-RID runtime packaging; semantic snapshot/click/fill/check integration is deferred to a separate agentic-browser pass;
- a live local shell with one cross-platform libghostty-vt state engine, Porta.Pty transport, and ordinary Avalonia-managed renderer on macOS, Windows, and Linux;
- keyboard/IME, mouse, focus, resize, clipboard, terminal-screen reads, typed waits, and executable programmatic terminal input with physical-human preemption of agent input;
- deterministic domain, application, protocol, agent-kernel, terminal, provider, persistence, and architecture tests.

## Stack

- [.NET 10 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Avalonia 12](https://docs.avaloniaui.net/docs/get-started/)
- [libghostty-vt](https://github.com/ghostty-org/ghostty) through an isolated C ABI for canonical terminal state and protocol encoding
- [Porta.Pty](https://github.com/IvanJosipovic/Porta.Pty) for PTY process transport on every supported desktop OS

libghostty-vt's API is not stable yet. GhostSHELL pins Ghostty commit `08f039fbb3dea9c6b1cdb5ff4550666598122346`, applies a narrow reviewed patch overlay in a disposable checkout, and keeps every Ghostty C declaration private to `GhostShell.Terminal`. The overlay exposes normalized OSC 133 lifecycle events, canonical virtual Kitty placement geometry, and Ghostty-backed full-scrollback search; it also enables Ghostty's existing Wuffs PNG decoder and publishes an exact extension-ABI marker. Application-owned render DTOs carry damage, live cursor state, underline variants/colors, and Kitty image placement/lifecycle into the Avalonia control; no Ghostty or Porta.Pty type crosses into Core, Protocol, SessionHost, or App.

The terminal is not a native child view. macOS, Windows, and Linux all use the same Avalonia presentation and input path; GhostSHELL does not embed Ghostty's `NSView`, use `NativeControlHost` for terminals, or use an IOSurface handoff. See [ADR 0040](./docs/adr/0040-cross-platform-libghostty-vt-terminal.md).

## Run

Install the workspace-local .NET SDK, then build the pinned libghostty-vt runtime for the current host:

```sh
GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh
```

The first native build downloads the pinned Zig toolchain and Ghostty source and can take several minutes. The build applies the reviewed overlay to a disposable checkout, runs Ghostty's patched VT tests, verifies every managed import plus the exact GhostSHELL extension ABI, and publishes the library, reviewed export manifest, license, receipt, and pinned Bash/Fish/Zsh integration resources under `native/artifacts/<rid>`. It also fetches the official JetBrains Mono 2.304 dependency declared by that Ghostty pin, verifies the regular/bold/italic/bold-italic faces and OFL by exact hash, and publishes their independently receipted cross-platform closure under `native/artifacts/common`.

Build, test, and run:

```sh
./scripts/check.sh
./.dotnet/dotnet run --project src/GhostShell.Desktop/GhostShell.Desktop.csproj
```

Verify the real PTY/libghostty-vt pipeline, split UTF-8 input, render damage,
terminal-controlled cursor and underline state, semantic shell events, PTY
flush, and process exit. Governed provider-to-terminal coverage remains in the
runtime/broker/session-host suites:

```sh
./.dotnet/dotnet test tests/GhostShell.Terminal.Tests/GhostShell.Terminal.Tests.csproj
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
tampered NuGet archives, and existing destinations. It validates the single
published `libghostty-vt.dylib`, its reviewed export manifest, pinned build
receipt, Ghostty license, and staged shell-integration manifest, emits deterministic SPDX 2.3 evidence
for the managed dependency closure, separately validates the exact JetBrains
Mono quartet, font manifest, source catalog, build receipt, and OFL, then
validates the exact bundle identity
through the acceptance fingerprint boundary. See the
[macOS packaging guide](./docs/macos-packaging.md) for guarantees and the
remaining native-license, signing, notarization, and named-host gates.

The desktop composition root selects one libghostty-vt/Porta.Pty engine and one Avalonia terminal presentation on macOS, Windows, and Linux. Packaged interactive-TUI rendering, physical input, IME, clipboard, mouse, resize, sleep/wake, and PTY lifecycle runs on named supported systems remain release acceptance requirements until their platform jobs have produced passing evidence. Use [`scripts/platform-terminal-acceptance.ps1`](./scripts/platform-terminal-acceptance.ps1) and the [platform acceptance guide](./docs/platform-terminal-acceptance.md) to capture host- and package-specific evidence without treating an unrun checklist as a pass.

Physical keyboard and screen-reader acceptance is likewise an explicit named-host gate. Use [`scripts/platform-accessibility-acceptance.ps1`](./scripts/platform-accessibility-acceptance.ps1) with the [VoiceOver, Narrator, and Orca acceptance guide](./docs/platform-accessibility-acceptance.md); the runner binds observations to one package and reader identity, retains stable descendant identities through cleanup, and emits a strict sanitized receipt.

OS-global Quick Terminal shortcuts use Carbon on macOS, `RegisterHotKey` on Windows, and `XGrabKey` in real X11 sessions. Wayland is reported as unsupported until a compositor-global portal backend can be implemented and verified safely.

Unsafe terminal paste fails closed into an explicit in-terminal confirmation prompt. The managed surface supports bounded local scrollback, mouse selection, policy-gated clipboard gestures, and OSC 8 `http`/`https` activation through a revalidated confirmation prompt. Brokerless process-originated OSC 52 access fails closed.

## Solution

- `src/GhostShell.Core`: framework-independent IDs, definitions, invariants, and state machines.
- `src/GhostShell.Application`: typed application/session operations, results, lifecycle, capability, attachment, input-lease, and engine ports.
- `src/GhostShell.Protocol`: versioned transport envelopes and stream contracts.
- `src/GhostShell.Agent`: provider-neutral native conversation loop, strict stream reduction, inert tool proposals, bounded run events, cancellation fencing, and atomic compaction.
- `src/GhostShell.Agent.Providers`: native Anthropic and OpenAI-compatible model discovery and streaming adapters, exact-scope vault resolution, bounded HTTP/SSE parsing, and provider bindings for the governed runtime.
- `src/GhostShell.Agent.Runtime`: exact-target provider/tool orchestration that converts inert proposals into closed typed terminal or browser requests and returns bounded structured results.
- `src/GhostShell.SessionHost`: in-process runtime registry, ordered events, revisions, attachments, leases, browser-domain policy, and close policy.
- `src/GhostShell.Terminal`: the cross-platform libghostty-vt state/input adapter and Porta.Pty process transport behind render-state, automation, typed input, and lifecycle ports.
- `src/GhostShell.Monitoring`: package-free, cross-platform local resource sampling behind privacy-bounded statistics and process-session ports.
- `src/GhostShell.App`: Avalonia presentation based on `design/design.pen`; it depends only on application ports and Core projections.
- `src/GhostShell.Desktop`: executable composition root and platform adapter registrations.
- `tools/GhostShell.Packaging`: fail-closed release-candidate bundle assembly.
- `native/ghostty-vt`: the reviewed patch overlay and notices applied to the pinned libghostty-vt build.
- `tests/*`: focused domain, application, protocol, terminal, session-host, provider, persistence, desktop, and architecture suites.

See [architecture.md](./docs/architecture.md) for boundaries and the implementation sequence.
