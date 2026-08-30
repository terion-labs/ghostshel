# ADR 0052: Source-aware Velopack updates

**Status:** Accepted
**Date:** 2026-08-30

## Context

GhostSHELL currently publishes a signed and notarized macOS archive through
GitHub Releases. Future builds may come from the Apple App Store, Microsoft
Store, or a Linux package manager. An app-store build must not replace itself
with a GitHub package, and a direct build should not send users to a browser for
an update it can safely download itself.

The update decision must come from the installed artifact. Runtime platform
detection cannot distinguish a GitHub build from an App Store build on the same
machine.

## Decision

Every packaged application carries a strict `distribution.json` manifest. It
records the distribution source, update strategy, package identifier, channel,
and runtime identifier. The app rejects unknown fields, mismatched package or
runtime identifiers, and invalid source/strategy pairs. The direct feed host and
repository remain compiled into the provider, so local manifest tampering cannot
redirect downloads.

Direct GitHub builds use Velopack 1.2.0. The channel combines runtime and track,
for example `osx-arm64-stable`, so a feed cannot cross operating systems or CPU
architectures. Update checks run only after the user selects "Check for
updates". A second action downloads the selected package. "Restart to update"
arms Velopack's external updater and then requests GhostSHELL's normal shutdown,
which preserves the existing session, recovery, database, and browser cleanup.
Automatic startup checks and automatic startup application are disabled.

Store and package-manager builds use `platform-managed`. They display their
install source and expose no Velopack actions. Development builds without a
trusted manifest also expose no update actions.

`VelopackApp.Run()` is the first operation in the desktop entry point, before
private helpers, CEF subprocess dispatch, or Avalonia setup. This ordering is a
Velopack process contract, not an update check.

## Release transition

The application runtime, source manifest, package inventory, and pinned `vpk`
tool land before the release lane changes format. Existing ZIP installations do
not contain Velopack's `UpdateMac` and `sq.version`, so `UpdateManager.IsInstalled`
is false and the About page explains that the bundle cannot update in place.

The release lane may publish a Velopack feed only after it moves Velopack bundle
assembly before GhostSHELL's nested code signing and notarization pass. The final
portable archive and full update package must then pass the same exact-artifact
codesign, Gatekeeper, notarization, source-seal, and release-evidence checks as
the current archive. Publishing packages created after those checks would mutate
the reviewed app bundle and is prohibited.

## Consequences

The UI and application layer do not depend on Velopack. Adding a store channel
requires a new manifest source/strategy pair and composition, not conditionals
inside the direct updater. GitHub checks remain user initiated. Downloads use
Velopack's package checksum and cache, and installation waits for a graceful app
exit.

Velopack can request elevation for a bundle in `/Applications`. GhostSHELL does
not offer download or apply actions for those system-wide installs, avoiding a
privileged local-package replacement path; they require the signed installer.
App Store builds remain sandboxed and platform managed; Velopack does not support
the macOS App Sandbox.
