# macOS release-candidate packaging

GhostSHELL can produce a real, self-contained macOS application bundle for local
release validation. The current candidate is arm64-only and unsigned.

## Prerequisites

Run the pinned native bootstrap on an Apple silicon Mac:

```sh
./scripts/bootstrap.sh
```

The packager fails closed when the GhostSHELL shim, libghostty, Ghostty resources,
terminfo, or Ghostty license is absent. It never substitutes the managed terminal
renderer into a package advertised as the native macOS candidate. Packaging also
requires the reviewed [`licenses/managed-components.json`](../licenses/managed-components.json)
catalog and the exact NuGet archives in the configured global package cache. The
native payload must also carry a validated build receipt whose artifact and
packaged-payload manifests match the reviewed native component catalog.

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
script publishes `osx-arm64` self-contained output, rejects symbolic links and
special entries, verifies the arm64 Mach-O executable and native libraries,
checks the shim's colocated libghostty linkage, assembles the bundle through the
tested packaging tool, validates `Info.plist`, and fingerprints the finished
package through the same boundary used by accessibility acceptance.
First-party portable PDBs are excluded from the application bundle, and the
script rejects any first-party assembly that embeds the physical build-host
repository path.

Before publishing, the packaging tool compares the complete
`GhostShell.deps.json` library set with the reviewed catalog. It validates the
exact .NET 10 `osx-arm64` target pair and runtime fallback chain, component-set
closure, resolved dependency edges, reachability, cycles, bounded asset shapes,
canonical dependency paths, NuGet content hashes, raw archive SHA-512 receipts,
and exact nuspec root, namespace, identity, version, and license metadata.
Unknown, missing, malformed, ambiguous, or tampered evidence fails the build.
Generated evidence is incrementally bounded before archive content is allocated,
and the SPDX writer cannot exceed its remaining package byte budget.

The bundle declares:

- bundle identifier `app.ghostshell`;
- executable `Contents/MacOS/GhostShell`;
- minimum system version macOS 13;
- the supplied `CFBundleShortVersionString` and `CFBundleVersion`;
- the pinned Ghostty license at
  `Contents/Resources/Licenses/GHOSTTY-LICENSE`;
- deterministic SPDX 2.3 evidence at
  `Contents/Resources/Licenses/SBOM.spdx.json`;
- exact `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` evidence extracted from the
  reviewed macOS SkiaSharp and HarfBuzzSharp native-asset archives.

The SPDX document describes one GhostSHELL root and records the exact managed
dependency closure, the exact .NET runtime archive, all fifteen published
GhostSHELL assemblies, and both published Ghostty dylibs. It deliberately uses
`NOASSERTION` where the current evidence cannot support a narrower license
conclusion. It is deterministic, contains no local package-cache paths, and is
not a claim that the statically linked Ghostty component graph is complete.

The bundle also retains the reviewed native component catalog, native build
receipt, normalized build evidence, and resource evidence under
`Contents/Resources/Licenses/Native`. The build evidence binds the selected
Ghostty source closure, build options, target/CPU settings, SDK inputs, verified
Zig manifest records, and the ordered static-archive selection without exposing
host-local paths. Two independent fresh local builds produced the same shipped
`libghostty.dylib` and byte-identical normalized evidence. This does not prove
cross-host reproducibility or byte-exact contribution from every intermediate
static archive; both remain explicit release blockers.

Packaging is non-destructive. A pre-existing destination is never overwritten,
assembly is staged in a private sibling directory and validated there, and the
validated candidate is published by one no-overwrite directory rename. If
assembly or validation fails, the private candidate is removed and the final
destination remains absent. Full package fingerprinting and the exclusive
rename run in one process; there is no standalone unvalidated publish command.

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

This checks the exact bundle identity, executable mode, regular-file boundary,
file-count and size limits, and full package manifest. It prints package and
executable digests but does not launch the candidate.

## Current packaging verification

A fresh local packaging verification on 2026-07-25 exercised the current
fifteen-assembly set, including `GhostShell.Mcp.dll`, through publish,
managed-component closure, SPDX generation, bundle fingerprinting, and
accessibility package inspection. The temporary unsigned candidate contained
752 regular files. Its executable SHA-256 was
`ebc98258bf63addd84de748a90de67fd2d7234de194c9d79c80c5633c6567c38`,
its package-manifest SHA-256 was
`3e39d4257ef6a90c7d3851af4b1e9cf7092905df1d7c3be83efba26c06cc1f02`,
and its SPDX SHA-256 was
`4a4f9252c126b05f8c317506246774a013df1cfaccf824d06fc5c8af0fbd8ef2`.
The 65-package SPDX document includes `GhostShell.Mcp` `0.1.0`,
`ModelContextProtocol.Core` `1.3.0` with declared `Apache-2.0`,
`Microsoft.Extensions.AI.Abstractions` `10.5.2` with declared `MIT`, and
`Microsoft.Extensions.Logging.Abstractions` `10.0.7` with declared `MIT`.
The candidate was temporary verification evidence, not a retained or named
release artifact.

The packaged `libghostty.dylib` SHA-256 is
`214412686c9de99efbde90b3df59e8a6ac3904c14f0beacb9e1889feebecb01b`.
Its normalized build-evidence SHA-256 is
`79b782c2ea48a2db536664c6fee4eeaae763b2fedf63101e2ea99fe9922456b7`;
the native artifact receipt records 476 files, 17,691,702 bytes, artifact
manifest
`64648f89a0f7b7b6dc34d7c721b528bcd50a6bc83036c1c1081c43b3472d9424`.
The 475-file, 17,614,222-byte packaged payload has manifest
`967db9dd9f499c0ef8e9c15eb7710497b274aed381cd95f2de3288f799fcdb81`.
The reviewed physical-input-barrier shim and its native smoke executable have
SHA-256 values
`68bc61937343a996e8a765e5e29eb45d275af7aa35c482d7a28490cc21217334`
and
`1be3788492e19908c98b882c84a5dba0985e892d9a997d7cdf63ca08e1afb04e`,
respectively.

## Outstanding release work

This is not a distributable release. Developer ID signing, hardened-runtime
entitlements, notarization, stapling, an icon pipeline, DMG or PKG creation,
update-feed policy, and named-host launch/accessibility acceptance remain
separate release gates. Those steps require product decisions or external Apple
credentials and must not be represented by an ad-hoc or unsigned local build.

The license gate also remains open. The package does not yet contain the full
license/source/relinking evidence for SMBLibrary, statically linked
gettext/libintl and other Ghostty dependencies, GPL-covered shell resources,
embedded fonts, or complete license mapping for the retained native source and
resource evidence. The catalog, SPDX comment, and third-party notice keep those
blockers explicit rather than presenting provenance evidence as legal
clearance.
