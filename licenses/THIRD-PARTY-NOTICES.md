# GhostSHELL third-party notices

This file records the managed packages resolved for the GhostSHELL desktop
project and indexes the runtime notices currently bundled with the application.
Package authors retain all rights granted by their respective licenses.
GhostSHELL does not claim ownership of these components.

The application bundle also includes:

- `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` for the
  self-contained .NET runtime;
- `GHOSTTY-LICENSE` for the pinned Ghostty runtime (`v1.3.1`, commit
  `332b2aefc6e72d363aa93ab6ecfc86eeeeb5ed28`);
- package-specific copyright and repository metadata in the corresponding
  NuGet packages.

The pinned Ghostty resources also contain bash and zsh integration derived in
part from Kitty under GPL-3.0-or-later, as declared in those files, and
`bash-preexec` under MIT. The release license gate remains open until the
corresponding complete license texts, copyright notices, source offer/link, and
the statically linked native dependency inventory are generated and verified
for the exact package payload.

The gate also remains open for `SMBLibrary` (`LGPL-3.0-or-later`), the pinned
Ghostty build's statically linked GNU gettext/libintl 0.24 (`LGPL-2.1`), and
the complete OFL license/reserved-font-name evidence for embedded Inter font
assets. The managed-component SBOM records these as unresolved evidence
blockers; it is not legal clearance and does not substitute for the complete
native dependency graph, exact source provenance, patches, build flags, license
texts, notices, source delivery, or relinking review.

SPDX license texts are available from <https://spdx.org/licenses/>. The table
below is a conservative inventory of the resolved `GhostShell.Desktop` managed
dependency graph. It includes platform-specific packages resolved for
supported targets even when a package is not copied into or loaded by the
current operating-system package. It is not an assertion that every listed
package ships in every build, nor a substitute for the release's exact native
software-bill-of-materials and license review.

| Package | Version | License |
|---|---:|---|
| `AWSSDK.Core` | `4.0.100.6` | Apache-2.0 |
| `AWSSDK.S3` | `4.0.101.3` | Apache-2.0 |
| `Avalonia` | `12.0.1` | MIT |
| `Avalonia.Angle.Windows.Natives` | `2.1.25547.20250602` | BSD-3-Clause |
| `Avalonia.BuildServices` | `11.3.2` | MIT |
| `Avalonia.Controls.WebView` | `12.0.1` | MIT |
| `Avalonia.Desktop` | `12.0.1` | MIT |
| `Avalonia.Fonts.Inter` | `12.0.1` | MIT |
| `Avalonia.FreeDesktop` | `12.0.1` | MIT |
| `Avalonia.FreeDesktop.AtSpi` | `12.0.1` | MIT |
| `Avalonia.HarfBuzz` | `12.0.1` | MIT |
| `Avalonia.Native` | `12.0.1` | MIT |
| `Avalonia.Remote.Protocol` | `12.0.1` | MIT |
| `Avalonia.Skia` | `12.0.1` | MIT |
| `Avalonia.Themes.Fluent` | `12.0.1` | MIT |
| `Avalonia.Win32` | `12.0.1` | MIT |
| `Avalonia.X11` | `12.0.1` | MIT |
| `BouncyCastle.Cryptography` | `2.6.2` | MIT |
| `FluentFTP` | `54.2.0` | MIT |
| `FluentIcons.Avalonia` | `2.1.333` | MIT |
| `FluentIcons.Common` | `2.1.333` | MIT |
| `FluentIcons.Resources.Avalonia` | `2.1.333` | MIT |
| `HarfBuzzSharp` | `8.3.1.3` | MIT |
| `HarfBuzzSharp.NativeAssets.Linux` | `8.3.1.3` | MIT |
| `HarfBuzzSharp.NativeAssets.WebAssembly` | `8.3.1.3` | MIT |
| `HarfBuzzSharp.NativeAssets.Win32` | `8.3.1.3` | MIT |
| `HarfBuzzSharp.NativeAssets.macOS` | `8.3.1.3` | MIT |
| `MicroCom.Runtime` | `0.11.4` | MIT |
| `Microsoft.Data.Sqlite` | `10.0.10` | MIT |
| `Microsoft.Data.Sqlite.Core` | `10.0.10` | MIT |
| `Microsoft.Extensions.AI.Abstractions` | `10.5.2` | MIT |
| `Microsoft.Extensions.DependencyInjection` | `10.0.10` | MIT |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.10` | MIT |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.7` | MIT |
| `ModelContextProtocol.Core` | `1.3.0` | Apache-2.0 |
| `Porta.Pty` | `1.0.7` | MIT |
| `SMBLibrary` | `1.5.7.1` | LGPL-3.0-or-later |
| `SQLitePCLRaw.bundle_e_sqlite3` | `2.1.12` | Apache-2.0 |
| `SQLitePCLRaw.core` | `2.1.12` | Apache-2.0 |
| `SQLitePCLRaw.lib.e_sqlite3` | `2.1.12` | Apache-2.0 |
| `SQLitePCLRaw.provider.e_sqlite3` | `2.1.12` | Apache-2.0 |
| `SSH.NET` | `2025.1.0` | MIT |
| `SkiaSharp` | `3.119.3-preview.1.1` | MIT |
| `SkiaSharp.NativeAssets.Linux` | `3.119.3-preview.1.1` | MIT |
| `SkiaSharp.NativeAssets.WebAssembly` | `3.119.3-preview.1.1` | MIT |
| `SkiaSharp.NativeAssets.Win32` | `3.119.3-preview.1.1` | MIT |
| `SkiaSharp.NativeAssets.macOS` | `3.119.3-preview.1.1` | MIT |
| `System.Security.Cryptography.ProtectedData` | `10.0.10` | MIT |
| `Tmds.DBus.Protocol` | `0.92.0` | MIT |
| `Tmds.DBus.SourceGenerator` | `0.0.22` | MIT |
| `Unicode.net` | `2.0.0` | MIT |
| `Vanara.Core` | `4.2.1` | MIT |
| `Vanara.PInvoke.Kernel32` | `4.2.1` | MIT |
| `Vanara.PInvoke.Shared` | `4.2.1` | MIT |
| `Wcwidth` | `3.0.0` | MIT |
| `XTerm.NET` | `1.0.15` | MIT |

This notice is informational and is not legal advice. It must not be used as
evidence that the M4 release license gate is complete.
