# ADR 0041: CEF off-screen browser runtime

- Status: Accepted
- Date: 2026-08-08
- Supersedes: browser-engine and native-child-view decisions in
  [ADR 0004](0004-native-browser-adapters.md) and
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md)

## Context

The operating-system webview implementation made browser panels native child
views. That prevented reliable Avalonia z-order, clipping, transforms, and
overlays, and required separate WKWebView, WebView2, and WebKit behavior. The
browser is a first-class panel, so those composition differences are product
constraints rather than an acceptable implementation detail.

CEF supplies one Chromium engine and an off-screen rendering (OSR) contract on
macOS, Windows, and Linux. It also makes GhostSHELL responsible for a large
multiprocess runtime, Chromium security updates, sandboxing, helper processes,
licenses, signing, and orderly process shutdown.

The published Exclr8CEF 0.8 packages cannot supply that runtime: their managed
package graph references unavailable per-RID packages. The reviewed source
revision also needs GhostSHELL-specific lifecycle, request-policy, and security
fixes.

## Decision

GhostSHELL vendors Exclr8CEF commit
`7751a0b76cbabaf1fa81ef2b71b694a44c87f77e`, applies a hashed local patch set,
and pins CEF `150.0.9+g81b0088+chromium-150.0.7871.46`.

`GhostShell.Browser` is the only product project that references the binding.
Its public contracts remain engine-neutral. `CefBrowserView` hosts the binding's
CPU BGRA OSR control as an ordinary Avalonia visual, while `BrowserSurface`
owns logical state, origin containment, crash replacement, and deterministic
disposal. Layout rebuilds reparent the same visual and preserve the panel-owned
session attachment; they do not suspend, conceal, or recreate the browser.

CEF subprocess dispatch happens before single-instance, storage, or Avalonia
startup. Process initialization happens after Avalonia setup, uses an exact
runtime-version check, a private persistent browser profile, no remote debugging
port, an opt-in-disabled JavaScript bridge, and closed handlers for popups,
dialogs, downloads, permissions, authentication, and certificate exceptions.
Top-level navigation is admitted by a cancellable main-frame callback; the
resource-request gate separately prevents unapproved local-file subresources.
Callback and dispatcher failures deny requests.

Panel close removes the control from the visual tree before disposal. Shutdown
force-closes all remaining browsers, continues the external CEF pump until every
`OnBeforeClose` is observed, and only then calls `CefShutdown`.

The baseline renderer is classic CPU OSR. Its UI handoff coalesces pending
frames so a busy renderer cannot queue an unbounded sequence of full-frame
copies. Accelerated shared-texture rendering is not advertised: the selected
binding does not yet carry Linux DMA-BUF plane metadata through its ABI and its
Windows/macOS interop is not production-qualified.

The semantic snapshot/click/fill/check implementation is deliberately excluded
from this migration. Existing application contracts remain, but the production
CEF adapter fails those operations closed until the separate agentic-browser
design pass.

Native artifacts are built per RID into a private staging tree, verified against
the reviewed CEF catalog, and published with a receipt that binds the upstream
archive hashes, binding commit, local patch digest, and every staged file.
macOS packages contain the framework and five correctly named helper bundles in
`Contents/Frameworks`; Windows and Linux use a flat runtime closure beside the
app host.

## Security and release gates

- macOS and Linux builds default to the CEF sandbox and cannot silently opt out.
- CEF 150 Windows sandboxing requires a native bootstrap/CLR launcher. The
  current .NET-hosted shim rejects secure Windows configuration rather than
  claiming that a null sandbox pointer is safe. An explicitly unsandboxed build
  is development-only; Windows release remains blocked on that launcher.
- Linux release qualification must verify the installed `chrome-sandbox`
  ownership/mode or an accepted user-namespace configuration.
- Chromium updates follow an owned security SLA and rebuild/acceptance matrix
  for every supported RID.

## Consequences

- Browser panels participate in normal Avalonia composition and overlays.
- macOS, Windows, and Linux share Chromium behavior and one adapter boundary.
- Distribution size, memory use, packaging complexity, and security ownership
  increase materially.
- CPU OSR adds a CPU copy and texture upload per presented frame; accelerated
  rendering remains a measured follow-up, not a falsely portable switch.
- Windows production packaging is fail-closed until its sandbox bootstrap is
  implemented and qualified.

## Alternatives rejected

- Keeping host-native webviews preserves the composition defects that motivated
  the migration and retains three behavior matrices.
- Consuming the published Exclr8CEF NuGet packages cannot produce a complete
  native runtime and omits required fixes.
- Enabling shared textures without complete platform handle metadata and tested
  synchronization would make the fastest path the least reliable one.
- Reintroducing raw JavaScript automation during the renderer migration would
  mix the explicitly deferred agentic trust model into the engine boundary.
