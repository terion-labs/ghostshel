# macOS release-candidate packaging

GhostSHELL can produce a self-contained macOS application bundle for local
release validation. The current candidate is arm64-only and unsigned. Its
terminal runtime is the same managed-presentation pipeline used on Windows and
Linux: Porta.Pty transports raw process bytes, libghostty-vt owns canonical
terminal state and protocol encoding, and an ordinary Avalonia control renders
the terminal. The package does not contain the retired AppKit terminal shim or
full libghostty renderer.

## Prerequisites

Install the workspace-local .NET SDK and build the pinned native terminal and
SQL-language runtimes:

```sh
GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh --rid osx-arm64
./scripts/build-sql-language-worker.sh --local --rid osx-arm64
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

The build receipt binds the component catalog, target RID, source commit,
toolchain archive, target/build options, passing patched-test result, patch-set
digest, extension ABI, reviewed export-manifest digest, library digest, license
digest, and shell-integration manifest. A missing or dirty pinned source
checkout, patch or test failure, missing export, incompatible extension ABI,
hash mismatch, linked resource, unexpected Mach-O install name, or incomplete
output fails the native build before publication.

The SQL-language build separately compiles Calcite with GraalVM Native Image,
runs the linked executable's framed-protocol smoke test, and atomically publishes
`ghostshell-sql-language`, its resolved dependency list, third-party notices,
and `build-receipt.json`. The receipt binds the executable hash, `osx-arm64`
RID, `darwin-arm64` ABI, protocol version, minimum macOS version, legal-closure
format, dependency/document/review-required counts, and hashes of both legal
files.

## Build a candidate

Create the destination parent first, then supply explicit product and build
versions:

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output artifacts/macos-arm64-rc/GhostShell.app
```

The destination must be named `GhostShell.app` and must not already exist. The
script publishes an `osx-arm64` self-contained application, rejects symbolic
links and special entries, verifies that the apphost and libghostty-vt are
arm64 Mach-O files, and requires the library install name
`@rpath/libghostty-vt.dylib`. The dylib may depend only on macOS `libSystem` and
may export only the Ghostty C ABI.

The SQL-language worker is also mandatory. Packaging verifies its RID, ABI,
artifact name, protocol version, file type, and SHA-256 against the build
receipt. It then reads the Mach-O `LC_BUILD_VERSION` command, requires its
minimum OS version to match the receipt, and rejects any minimum newer than
macOS 13. The verified receipt must survive the self-contained publish and
final bundle byte-for-byte. Packaging also re-hashes the dependency manifest
and third-party notices before publish, after publish, and in the assembled
bundle; all three copies must match the receipt.

The temporary apphost's `LC_BUILD_VERSION` SDK field is updated to macOS 26.0
before package fingerprinting and is ad-hoc signed so the candidate remains
launchable. This opts native window chrome into the current macOS appearance;
it neither introduces a native terminal view nor raises the app's macOS 13
minimum system version. Release signing replaces this temporary signature.

The package assembly is non-destructive. It stages into a private sibling,
validates the complete candidate, then publishes with a no-overwrite directory
move. A failure removes the staging directory and leaves the requested
destination absent.

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

This is not a distributable release. Developer ID signing, hardened-runtime
entitlements, notarization, stapling, icon production, DMG or PKG creation, and
update-feed policy remain separate release work. They require product decisions
or external Apple credentials and must not be represented by an ad-hoc local
signature.

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
