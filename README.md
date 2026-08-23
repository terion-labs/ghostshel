# GhostSHELL

GhostSHELL is a cross-platform terminal workspace for local and remote sessions. Its agent foundation is native .NET: the provider-neutral kernel, provider adapters, and governed runtime run in-process and add neither a Node.js sidecar nor software to remote machines. Provider profiles cover Anthropic, OpenAI, Google, xAI, DeepSeek, Moonshot AI, OpenRouter, GitHub Copilot, Amazon Bedrock, Ollama, and custom OpenAI-compatible endpoints; credentials and OAuth sessions are stored behind OS-vault references, and protocols without a production adapter remain explicitly fail-closed. The agent is workspace-scoped in the desktop surface: it re-discovers supported live panels and rebuilds the tools supplied by the runtime's family contribution registry between rounds while preserving the workspace identity and revalidating every narrowed action. Terminal, browser, File Viewer, Process Monitor, Statistics, workspace-graph, and governed MCP operations all cross the session-host capability broker, policy, cancellation, and durable audit boundary. MCP supports both directly launched stdio servers and remote Streamable HTTP profiles. Browser snapshot, click, fill, and check contracts remain disabled in production pending native conformance and approval-context gates.

The repository contains a runnable vertical slice of the Pencil terminal-workspace design:

- durable workspaces, tabs, connections, screens, hosted terminal/File Viewer panels, live bounded local Statistics and Process Monitor panels, and metadata-only recent sessions;
- a docked workspace agent with streaming reasoning summaries and token usage, bounded image input for capable providers, reasoning-effort selection, steering and queued follow-ups, active-tool state, one-action approvals, cancellation, and an explicitly confirmed run-only full-access window for terminal actions; the native kernel keeps model tool calls inert until the trusted runtime and session host authorize one typed action;
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
./scripts/build-cef-runtime.sh --rid osx-arm64
```

The first native build downloads the pinned Zig toolchain and Ghostty source and can take several minutes. The build applies the reviewed overlay to a disposable checkout, runs Ghostty's patched VT tests, verifies every managed import plus the exact GhostSHELL extension ABI, and publishes the library, reviewed export manifest, license, receipt, and pinned Bash/Fish/Zsh integration resources under `native/artifacts/<rid>`. It also fetches the official JetBrains Mono 2.304 dependency declared by that Ghostty pin, verifies the regular/bold/italic/bold-italic faces and OFL by exact hash, and publishes their independently receipted cross-platform closure under `native/artifacts/common`.

Build, test, and run:

```sh
./scripts/check.sh --full
./.dotnet/dotnet run --project src/GhostShell.Desktop/GhostShell.Desktop.csproj
```

The repository gate is intentionally deterministic and warning-free. It uses
the exact SDK in `global.json`, locked NuGet restores with central package
versions and vulnerability auditing, `dotnet format`, all enabled compiler and
security analyzers, architecture contracts, and the test projects in a stable
sequence. `./scripts/check.sh --quick` runs the same restore, audit, formatting,
build, and architecture checks for fast iteration. Bootstrap installs the
checked-in pre-commit and pre-push hooks; they can be reinstalled with
`./scripts/install-hooks.sh`.

GitHub Actions runs the managed suite as six functional sections in parallel
on macOS, Linux, and Windows while retaining complete Release builds on every
platform. A `v<major>.<minor>.<patch>` tag, or a manual **Repository gate** run,
waits for those jobs and then builds the verified native dependencies on an
Apple Silicon runner. The run publishes `GhostShell-macOS-arm64-<version>.zip`
and its SHA-256 checksum as a 30-day workflow artifact. This first early-release
artifact is unsigned; use the signing and notarization flow documented below
before distributing outside a trusted tester group.

Dependency updates must refresh every graph that CI consumes: the ordinary
managed graph, the Windows-targeted managed graph, and both portable Linux
release RIDs. Regenerate them deliberately, review the lock-file diffs, and
then run the full gate:

```sh
./.dotnet/dotnet restore GhostShell.slnx --force-evaluate
./.dotnet/dotnet restore GhostShell.slnx \
  -p:GhostShellWindowsBuild=true --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime linux-x64 --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime linux-arm64 --force-evaluate
./scripts/check.sh --full
```

GitHub Copilot device authorization uses GitHub's public first-party Copilot
client identity by default. A distribution with its own registered GitHub OAuth
app can override the public client ID before launching the desktop process:

```sh
export GHOSTSHELL_GITHUB_OAUTH_CLIENT_ID="your-ghostshell-oauth-app-client-id"
```

OpenAI browser and device authorization likewise use OpenAI's public Codex
client identity and need no additional environment variable. Browser login owns
the registered `http://localhost:1455/auth/callback` listener for the bounded
duration of the flow; if another process owns that port, login fails closed.
GitHub's long-lived device token remains vault-only refresh material; GhostShell
exchanges it for a bounded Copilot API token before provider traffic and repeats
that exchange locally when the Copilot token expires.

On macOS, `dotnet run` assembles the framework-dependent build into a private
development `.app` under `src/GhostShell.Desktop/obj` before starting it. This
preserves CEF's required `Contents/Frameworks` layout without weakening the
verified native-payload checks used by release packaging.

Verify the real PTY/libghostty-vt pipeline, split UTF-8 input, render damage,
terminal-controlled cursor and underline state, semantic shell events, PTY
flush, and process exit. Governed provider-to-terminal coverage remains in the
runtime/broker/session-host suites:

```sh
./.dotnet/dotnet test tests/GhostShell.Terminal.Tests/GhostShell.Terminal.Tests.csproj
```

Build a non-launching, unsigned macOS arm64 Native AOT application-bundle candidate.
Install LLVM's `ld64.lld` first; the packager also accepts its absolute path
through `GHOSTSHELL_NATIVE_AOT_LINKER`:

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output artifacts/macos-arm64-rc/GhostShell.app
```

The packager emits a speed-optimized Native AOT executable with no managed
application DLLs, `.deps.json`, runtime configuration, or JIT runtime in the
bundle. It uses a separate locked self-contained publish only to validate the
reviewed managed dependency catalog and produce license evidence. The packager
refuses incomplete native payloads, dependency-catalog drift,
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
- `src/GhostShell.Agent`: provider-neutral native conversation loop, strict stream reduction, inert tool batches, bounded run events, reasoning/usage/image metadata, steering, cancellation fencing, atomic compaction, and idle checkpoint capture/restore.
- `src/GhostShell.Agent.Providers`: bounded native provider adapters, request-local API-key/OAuth resolution, OpenAI browser/device and GitHub device authorization, HTTP/SSE parsing, model discovery, and governed-runtime bindings.
- `src/GhostShell.Agent.Runtime`: workspace-scoped provider/tool orchestration with a family contribution registry filtered by live panels, sequential correlated tool batches, steering/follow-ups, and closed typed execution requests.
- `src/GhostShell.Mcp`: governed stdio and remote Streamable HTTP MCP sessions, bounded discovery/calls, and frozen run manifests.
- `src/GhostShell.SessionHost`: in-process runtime registry, ordered events, revisions, attachments, leases, browser action guards, and close policy.
- `src/GhostShell.Terminal`: the cross-platform libghostty-vt state/input adapter and Porta.Pty process transport behind render-state, automation, typed input, and lifecycle ports.
- `src/GhostShell.Monitoring`: package-free, cross-platform local resource sampling behind privacy-bounded statistics and process-session ports.
- `src/GhostShell.App`: Avalonia presentation; it depends only on application ports and Core projections.
- `src/GhostShell.Desktop`: executable composition root and platform adapter registrations.
- `tools/GhostShell.Packaging`: fail-closed release-candidate bundle assembly.
- `native/ghostty-vt`: the reviewed patch overlay and notices applied to the pinned libghostty-vt build.
- `tests/*`: focused domain, application, protocol, terminal, session-host, provider, persistence, desktop, and architecture suites.

See [architecture.md](./docs/architecture.md) for boundaries and the implementation sequence.
