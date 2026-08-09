# macOS release-candidate packaging

GhostSHELL can currently produce a self-contained macOS arm64 application
bundle for local release validation. Candidates are unsigned by default; the
same pipeline can apply nested Developer ID signatures and submit
the finished application for notarization when release credentials are
provided. Its
terminal runtime is the same managed-presentation pipeline used on Windows and
Linux: Porta.Pty transports raw process bytes, libghostty-vt owns canonical
terminal state and protocol encoding, and an ordinary Avalonia control renders
the terminal. The package does not contain the retired AppKit terminal shim or
full libghostty renderer.

## Prerequisites

Install the workspace-local .NET SDK and build the pinned native terminal
runtime:

```sh
GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh --rid osx-arm64
./scripts/build-cef-runtime.sh --rid osx-arm64
```

The native build checks out Ghostty commit
`08f039fbb3dea9c6b1cdb5ff4550666598122346`, applies the ordered patch overlay
from `native/ghostty-vt/patches` to a disposable checkout, builds the public
libghostty-vt C ABI with pinned Zig 0.16.0, runs Ghostty's patched
`test-lib-vt` suite, verifies the complete managed-import export set and exact
GhostSHELL extension ABI, and publishes:

- `native/artifacts/osx-arm64/libghostty-vt.dylib`;
- `GHOSTTY-LICENSE`;
- `ghostty-vt-required-exports.txt`;
- `native-terminal-build-receipt.json`;
- a manifest plus the reviewed Bash, Fish, and Zsh shell-integration resources
  copied byte-for-byte from that Ghostty commit.

The same successful build also verifies and atomically publishes the official
JetBrains Mono 2.304 regular, bold, italic, and bold-italic faces under
`native/artifacts/common/fonts/JetBrainsMono`. Zig fetches the exact URL and
package hash declared by the pinned Ghostty source. A separate reviewed
catalog, sorted manifest, OFL, and build receipt bind every font byte; Ghostty's
larger test-font resources are not packaged.

The CEF build consumes the exact release and Exclr8CEF source commit in
`licenses/cef-runtime-components.json`, verifies the already-patched vendored
source against the full-source and patch-set manifests, and uses disposable
build and staging trees to produce an explicit runtime root. The root
contains the CEF framework, Exclr8CEF shim, all five `GhostSHELL Helper` app
variants, CEF license and Chromium credits, binding license, and a sorted
file-level SHA-256 receipt. The packager validates that receipt before running
the more expensive desktop publish.

The build receipt binds the component catalog, target RID, source commit,
toolchain archive, target/build options, passing patched-test result, patch-set
digest, extension ABI, reviewed export-manifest digest, library digest, license
digest, and shell-integration manifest. A missing or dirty pinned source
checkout, patch or test failure, missing export, incompatible extension ABI,
hash mismatch, linked resource, unexpected Mach-O install name, or incomplete
output fails the native build before publication.

## Build a candidate

Create the destination parent first, then supply explicit product and build
versions:

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --runtime-identifier osx-arm64 \
  --output artifacts/macos-arm64-rc/GhostShell.app
```

The destination must be named `GhostShell.app` and must not already exist. The
script publishes an `osx-arm64` self-contained application, rejects symbolic
links and special entries, verifies that the apphost and libghostty-vt are
arm64 Mach-O files, verifies the CEF shim, framework libraries,
and five helper executables against that same architecture, and requires the
terminal library install name
`@rpath/libghostty-vt.dylib`. The dylib may depend only on macOS `libSystem` and
may export only the Ghostty C ABI.

The temporary apphost's `LC_BUILD_VERSION` SDK field is updated to macOS 26.0
before package fingerprinting and is ad-hoc signed so the candidate remains
launchable. This opts native window chrome into the current macOS appearance;
it neither introduces a native terminal view nor raises the app's macOS 13
minimum system version. Release signing replaces this temporary signature.

The package assembly is non-destructive. It stages into a private sibling,
validates the complete candidate, then publishes with a no-overwrite directory
move. A failure removes the staging directory and leaves the requested
destination absent.

The default CEF root is `native/artifacts/<rid>/cef`. A separately staged,
verified root can be supplied with `--cef-runtime-root`; it must still match
the checked-in catalog and its own receipt.

Standalone CEF runtime build, receipt, and catalog validation continue to
support `osx-x64`. Full Intel application packaging fails fast in this pass:
it requires a separate exact managed-component catalog and a verified x64
libghostty-vt payload/receipt before the app packager may claim that RID.

For a release candidate, use a Developer ID Application identity. The signing
script signs all managed-runtime leaf Mach-O files, CEF leaf libraries, the
framework, shared shims, five helper apps, and the outer app in nested-code
order without `codesign --deep` mutation:

```sh
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --runtime-identifier osx-arm64 \
  --output artifacts/macos-arm64-rc/GhostShell.app \
  --sign-identity "Developer ID Application: Example Corp (TEAMID)"
```

Add `--notary-profile ghostshell-release` to ZIP the candidate temporarily,
submit it with `notarytool --wait`, staple the accepted ticket, and validate it
with both `stapler` and Gatekeeper. The profile must already exist in the
login keychain; credentials are never accepted on the command line or written
to package evidence.

## Validated payload

The packager fails closed unless the self-contained publish contains:

- the GhostSHELL apphost, runtime configuration, and complete first-party
  assembly set;
- exactly the current terminal library `libghostty-vt.dylib` rather than
  `libghostshell-ghostty.dylib` or full `libghostty.dylib`;
- the pinned Ghostty license, native component catalog, and native build
  receipt;
- the exact reviewed libghostty-vt export manifest;
- the exact shell-integration manifest, notice, and reviewed Bash/Fish/Zsh
  files;
- the exact four-face JetBrains Mono closure, manifest, font catalog, build
  receipt, and retained OFL text;
- the reviewed managed-component catalog, NuGet archive evidence, .NET license,
  and third-party notices.
- the exact CEF 150 framework/resource/locale closure and Exclr8CEF shim;
- `GhostSHELL Helper.app` plus the Alerts, GPU, Plugin, and Renderer variants,
  each with matching executable and bundle identity;
- the CEF BSD license, Chromium `CREDITS.html`, Exclr8CEF MIT license, reviewed
  CEF catalog, file-level build receipt, and a deterministic CEF SPDX document.

Native provenance validation requires a passing patched-test receipt and the
exact extension ABI, then compares the packaged dylib, reviewed export
manifest, license, shell-integration manifest, and every staged shell resource
with the build receipt and native component catalog. Managed provenance validation compares
the complete `GhostShell.deps.json` closure with the reviewed catalog, NuGet
content hashes, archive SHA-512 receipts, nuspec identity/version/license
metadata, and resolved dependency graph. Unknown, missing, malformed,
ambiguous, linked, or tampered evidence fails assembly.

Terminal-font provenance separately checks the Ghostty dependency declaration,
official JetBrains Mono 2.304 Zig package hash, four exact TTF sizes and hashes,
TrueType headers, sorted manifest closure, exact catalog/receipt copies, and an
installed OFL copy identical to the packaged one. An extra face or file is a
package error, not an implicit extension point.

The bundle declares:

- bundle identifier `app.ghostshell`;
- executable `Contents/MacOS/GhostShell`;
- minimum system version macOS 13;
- the supplied `CFBundleShortVersionString` and `CFBundleVersion`;
- `Contents/Resources/Licenses/GHOSTTY-LICENSE`;
- exact native catalog/receipt copies under
  `Contents/Resources/Licenses/Native`;
- `Contents/Resources/Licenses/JetBrainsMono-OFL.txt` plus exact font
  catalog/receipt copies under `Contents/Resources/Licenses/Native`;
- deterministic managed dependency and third-party evidence under
  `Contents/Resources/Licenses`.
- `Contents/Frameworks/Chromium Embedded Framework.framework` and the five
  CEF subprocess helper app bundles;
- `libexclr8cef.dylib` in both `Contents/MacOS` for the managed host and
  `Contents/Frameworks` for helper-process `@rpath` resolution.

The shell-integration runtime assets and their manifest remain under
`Contents/MacOS/ghostty/shell-integration`, beside the managed application that
resolves them. The public terminal library remains beside the apphost at
`Contents/MacOS/libghostty-vt.dylib`.
The inspectable font closure remains under
`Contents/MacOS/fonts/JetBrainsMono`; Avalonia consumes the same verified
faces through its embedded-font collection.

## Inspect without launching

The package can be inspected without starting an application process:

```sh
./.dotnet/dotnet run \
  --project tools/GhostShell.AccessibilityAcceptance/GhostShell.AccessibilityAcceptance.csproj \
  --configuration Release \
  -- \
  inspect-package \
  --platform MacOS \
  --build-label macos-0.1.0-1 \
  --package artifacts/macos-arm64-rc/GhostShell.app
```

This checks bundle identity, executable mode, regular-file boundaries,
file-count and size limits, and the complete package manifest. It prints package
and executable digests but does not launch the candidate.

## Outstanding release work

The pipeline supports Developer ID signing, Chromium JIT hardened-runtime
entitlements, notarization, and stapling, but possession of credentials does
not itself make a distributable release. Independent license review, icon
production, DMG or PKG creation, update-feed policy, and release-identity
operations remain separate gates. An unsigned or ad-hoc local candidate must
not be represented as notarized.

The native component catalog deliberately reports `BLOCKED`: the exact linked
libghostty-vt and staged shell-integration source/license closure has not
completed independent release review. In particular, the retained Bash/Zsh
upstream notices include GPL-covered portions. The catalog, receipt, and notice
keep this blocker explicit rather than treating source provenance as legal
clearance.

Finally, a structurally valid bundle is not evidence that its interactive
terminal is ready to ship. Named-host rendering, full-screen TUI, physical
keyboard, IME, clipboard, mouse, resize, sleep/wake, PTY lifecycle, and
VoiceOver accessibility verification remain release gates for the exact
packaged candidate.
