# ADR 0051: Logical browser profiles with ephemeral CEF state

**Status:** Accepted
**Date:** 2026-08-28

## Context

Named browser profiles are useful for choosing isolation and bounded HTTP
authentication, but GhostSHELL cannot safely persist a Chromium profile today.
The vendored Exclr8CEF boundary can create private request contexts and clear
cookies and HTTP-auth credentials. It does not provide a reviewed, complete,
cancellable export/import contract for cookies, local storage, IndexedDB,
permissions, cache, service workers, or other Chromium state. Mounting a
decrypted CEF profile directory would also leave a large plaintext tree while
the app is running and would bypass the encrypted definition store.

## Decision

GhostSHELL stores versioned `BrowserProfileDefinition` metadata in its encrypted
definition catalog. `DurableMetadata` means the name, enabled state, privacy
policy, and optional bounded HTTP-auth `SecretRef` survive restart. It does not
mean Chromium state survives. `PrivateSession` gives each panel a separate
in-memory partition. Both policies keep cookies, local storage, IndexedDB,
cache, navigation state, and other web content in process memory only.
Permission requests and downloads remain blocked by the browser host.

Every browser panel pins an exact profile definition and catalog revision for
its lifetime. Popups and renderer replacements reuse that binding. A missing or
disabled profile produces a visible unavailable panel; the runtime never falls
back to another profile. The built-in profile preserves the existing shared or
per-workspace partition choice, so the catalog addition does not merge legacy
cookie jars.

`CefBrowserProfileStore` creates request contexts without `cache_path` and
destroys local and routed contexts when their final lease ends. Clear-data
requests bind profile id, partition, revision, and explicit categories. Cookies
and HTTP-auth credentials may be cleared on the exact live contexts supported
by Exclr8CEF. Whole-profile reset is valid only after every lease for that exact
revision is gone. Settings does not offer a broad clear for private-session or
per-workspace partitions whose exact identity it cannot know, and never deletes
downloaded user files.

Optional HTTP-auth metadata is limited to exact host, optional port and realm,
Basic or Digest scheme, username, and a vault `SecretRef`. The CEF adapter
resolves it only for a matching non-proxy challenge and does not expose the
secret to the catalog, UI projection, recovery payload, or portable bundle.
Bundles omit the built-in profile, strip browser credential references, and
import custom profiles disabled with authentication detached.

Runtime recovery records profile id and the exact logical partition identity.
Recovery re-resolves the current catalog revision and leaves a visible repair
state if the profile is missing or disabled. OAuth is an explicit
user-initiated “Open in system browser” action; navigation is not inspected for
redirect heuristics.

## Consequences

- A durable logical profile can share one in-memory session across panels in a
  process, but signing in again is expected after its final lease or app exit.
- The UI must consistently say “durable settings, temporary web data.”
- No persistent CEF cache path, mounted decrypted directory, cookie
  import/export, or plaintext browser-state tree is permitted by this decision.
- Supporting durable web content later requires a separate reviewed encrypted
  storage design and CEF capability boundary.
