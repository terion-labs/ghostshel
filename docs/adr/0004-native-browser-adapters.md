# ADR 0004: Native browser adapters with an engine-neutral contract

- Status: Accepted
- Date: 2026-07-22

## Context

Desktop browser panels need platform-native engines, while application and agent operations require one predictable automation surface. Native views also have z-order and focus constraints under Avalonia.

## Decision

Use `WKWebView` on macOS, WebView2 on Windows, and WPE WebKit on Linux with a WebKitGTK fallback behind `GhostShell.Browser` ports. Each adapter owns its native profile, view, permission, download, dialog, crash, and certificate behavior. The target common contract exposes navigation, accessibility/DOM-derived snapshots, short-lived element references, interaction, waits, screenshots, and explicit capability negotiation.

Unsupported optional operations return `capability_not_supported`; adapters never simulate success. Element references expire on navigation or document revision. Domain policy and the session-host capability broker run before every human or agent browser action, and page content is labeled untrusted.

Browser and terminal native views use rectangular, non-transformed hosts. Workflows that cannot reliably overlay a native child view use a docked sibling, separate top-level, or platform-native sheet.

The first implemented capability slice is intentionally limited to typed state,
navigation, back, forward, reload, and stop through the official Avalonia native
webview wrapper. [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md)
records that phased boundary; the broader automation contract above remains the
target rather than an implied current capability.

## Consequences

- Browser behavior follows the host OS and avoids shipping an additional Chromium runtime on every platform.
- Conformance tests define the common subset and record richer platform capabilities.
- Three platform adapters and CI environments are required.
- Future server mode needs a separate server/browser capability rather than pretending native desktop views exist in WASM.

## Alternatives rejected

- Assuming Chrome DevTools Protocol everywhere excludes WKWebView, WPE WebKit, and WebKitGTK.
- Embedding page objects or native handles in protocol DTOs prevents transport evolution.
- Silent polyfills hide security and correctness differences between engines.
