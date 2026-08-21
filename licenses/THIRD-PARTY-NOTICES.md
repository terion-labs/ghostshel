# GhostSHELL third-party notices

This file records the managed packages resolved for the GhostSHELL desktop
project and indexes the runtime notices currently bundled with the application.
Package authors retain all rights granted by their respective licenses.
GhostSHELL does not claim ownership of these components.

The application bundle also includes:

- `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` for the
  self-contained .NET runtime;
- `GHOSTTY-LICENSE` for the pinned libghostty-vt source snapshot (commit
  `08f039fbb3dea9c6b1cdb5ff4550666598122346`);
- `JetBrainsMono-OFL.txt` for the embedded JetBrains Mono 2.304 regular,
  bold, italic, and bold-italic terminal faces;
- `CEF-LICENSE.txt` and `Chromium-CREDITS.html` for the pinned Chromium
  Embedded Framework runtime;
- `Exclr8CEF-MIT.txt` for GhostSHELL's pinned, patched Exclr8CEF binding;
- `runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md` and
  `runtime-dependencies.txt` for the separately receipted native SQL language
  worker closure;
- Lucide `panel-left-close`, `panel-right-close`, `panel-bottom-close`,
  `panel-top-close`, and `fullscreen` vector geometry under the ISC license;
- package-specific copyright and repository metadata in the corresponding
  NuGet packages.

The pinned Ghostty resources also contain bash and zsh integration derived in
part from Kitty under GPL-3.0-or-later, as declared in those files, and
`bash-preexec` under MIT. The release license gate remains open until the
corresponding complete license texts, copyright notices, source offer/link, and
the statically linked native dependency inventory are generated and verified
for the exact package payload.

The gate also remains open for `SMBLibrary` (`LGPL-3.0-or-later`) and the
complete OFL license/reserved-font-name evidence for embedded Inter font
assets. JetBrains Mono is tracked separately by an exact source catalog,
per-face hashes, build receipt, package manifest, and retained OFL text. The
managed-component SBOM records the remaining blockers as unresolved evidence;
it is not legal clearance and does not substitute for the complete
native dependency graph, exact source provenance, patches, build flags, license
texts, notices, source delivery, or relinking review.

CEF is tracked separately by `cef-runtime-components.json`, a per-RID exact
build receipt, retained CEF license and Chromium credits, and a CEF-specific
SPDX document. Those artifacts preserve provenance; they are not legal
clearance and do not replace independent review of Chromium's generated
third-party credits or an owned security-update SLA.

The native SQL language worker is tracked separately by its per-RID build
receipt, runtime dependency inventory, and nested third-party notice. Those
components are intentionally not duplicated in the managed NuGet table below.

SPDX license texts are available from <https://spdx.org/licenses/>.
The table below is the conservative managed third-party inventory resolved
by the reviewed GhostSHELL 0.1.0 `osx-arm64` publish. It contains 121 NuGet
packages and the two separately licensed vendored Exclr8CEF projects.
First-party GhostSHELL project assemblies are omitted; the self-contained
.NET runtimepack is indexed by the retained .NET license and notice files
rather than duplicated here.

This inventory is RID-specific. Every other release RID must regenerate and
validate its own exact managed-component catalog and SBOM against its publish
output before distribution. This table does not claim a cross-target inventory
and does not substitute for the release's exact native software bill of
materials and independent license review.

| Package | Version | License |
|---|---:|---|
| `AWSSDK.Core` | `4.0.100.6` | Apache-2.0 |
| `AWSSDK.S3` | `4.0.101.3` | Apache-2.0 |
| `Avalonia.AvaloniaEdit` | `12.0.0` | MIT |
| `Avalonia.Controls.ColorPicker` | `12.0.5` | MIT |
| `Avalonia.Controls.DataGrid` | `12.0.1` | MIT |
| `Avalonia.Desktop` | `12.0.5` | MIT |
| `Avalonia.Fonts.Inter` | `12.0.5` | MIT |
| `Avalonia.FreeDesktop.AtSpi` | `12.0.5` | MIT |
| `Avalonia.FreeDesktop` | `12.0.5` | MIT |
| `Avalonia.HarfBuzz` | `12.0.5` | MIT |
| `Avalonia.Native` | `12.0.5` | MIT |
| `Avalonia.Remote.Protocol` | `12.0.5` | MIT |
| `Avalonia.Skia` | `12.0.5` | MIT |
| `Avalonia.Themes.Fluent` | `12.0.5` | MIT |
| `Avalonia.Win32` | `12.0.5` | MIT |
| `Avalonia.X11` | `12.0.5` | MIT |
| `Avalonia` | `12.0.5` | MIT |
| `AvaloniaEdit.TextMate` | `12.0.0` | MIT |
| `Azure.Core` | `1.38.0` | MIT |
| `Azure.Identity` | `1.11.4` | MIT |
| `BouncyCastle.Cryptography` | `2.6.2` | MIT |
| `ClickHouse.Client` | `7.8.0` | MIT |
| `Dock.Avalonia.Themes.Fluent` | `12.0.0.2` | MIT |
| `Dock.Avalonia` | `12.0.0.2` | MIT |
| `Dock.Controls.DeferredContentControl` | `12.0.0.2` | MIT |
| `Dock.Controls.ProportionalStackPanel` | `12.0.0.2` | MIT |
| `Dock.Controls.Recycling.Model` | `12.0.0.2` | MIT |
| `Dock.Controls.Recycling` | `12.0.0.2` | MIT |
| `Dock.MarkupExtension` | `12.0.0.2` | MIT |
| `Dock.Model.Inpc` | `12.0.0.2` | MIT |
| `Dock.Model` | `12.0.0.2` | MIT |
| `Dock.Settings` | `12.0.0.2` | MIT |
| `DuckDB.NET.Bindings.Full` | `1.2.1` | NOASSERTION (nuspec file: `LICENSE.md`) |
| `DuckDB.NET.Data.Full` | `1.2.1` | NOASSERTION (nuspec file: `LICENSE.md`) |
| `ExCSS` | `4.3.1` | MIT |
| `Exclr8Cef.WebView` | `0.8.0` | MIT (vendored project; see `Exclr8CEF-MIT.txt`) |
| `Exclr8Cef` | `0.8.0` | MIT (vendored project; see `Exclr8CEF-MIT.txt`) |
| `FirebirdSql.Data.FirebirdClient` | `10.3.1` | NOASSERTION (nuspec file: `license.txt`) |
| `FluentFTP` | `54.2.0` | MIT |
| `FluentIcons.Avalonia` | `2.1.333` | MIT |
| `FluentIcons.Common` | `2.1.333` | MIT |
| `FluentIcons.Resources.Avalonia` | `2.1.333` | MIT |
| `HarfBuzzSharp.NativeAssets.macOS` | `8.3.1.3` | MIT |
| `HarfBuzzSharp` | `8.3.1.3` | MIT |
| `LiteDB` | `5.0.21` | MIT |
| `Magick.NET-Q8-AnyCPU` | `14.16.0` | Apache-2.0 |
| `Magick.NET.Core` | `14.16.0` | Apache-2.0 |
| `Markdig` | `1.3.2` | BSD-2-Clause |
| `Mermaider` | `0.12.1` | MIT |
| `MicroCom.Runtime` | `0.11.4` | MIT |
| `Microsoft.Bcl.AsyncInterfaces` | `1.1.1` | MIT |
| `Microsoft.Bcl.Cryptography` | `9.0.4` | MIT |
| `Microsoft.Data.SqlClient` | `6.0.2` | MIT |
| `Microsoft.Data.Sqlite.Core` | `10.0.10` | MIT |
| `Microsoft.Extensions.AI.Abstractions` | `10.5.2` | MIT |
| `Microsoft.Extensions.Caching.Abstractions` | `9.0.4` | MIT |
| `Microsoft.Extensions.Caching.Memory` | `9.0.4` | MIT |
| `Microsoft.Extensions.Configuration.Abstractions` | `8.0.0` | MIT |
| `Microsoft.Extensions.Configuration.Binder` | `8.0.0` | MIT |
| `Microsoft.Extensions.Configuration` | `8.0.0` | MIT |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.10` | MIT |
| `Microsoft.Extensions.DependencyInjection` | `10.0.10` | MIT |
| `Microsoft.Extensions.Diagnostics.Abstractions` | `8.0.0` | MIT |
| `Microsoft.Extensions.Diagnostics` | `8.0.0` | MIT |
| `Microsoft.Extensions.Http` | `8.0.0` | MIT |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.7` | MIT |
| `Microsoft.Extensions.Logging` | `8.0.0` | MIT |
| `Microsoft.Extensions.ObjectPool` | `10.0.3` | MIT |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | `8.0.0` | MIT |
| `Microsoft.Extensions.Options` | `9.0.4` | MIT |
| `Microsoft.Extensions.Primitives` | `9.0.4` | MIT |
| `Microsoft.IO.RecyclableMemoryStream` | `3.0.1` | MIT |
| `Microsoft.Identity.Client.Extensions.Msal` | `4.61.3` | MIT |
| `Microsoft.Identity.Client` | `4.61.3` | MIT |
| `Microsoft.IdentityModel.Abstractions` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.JsonWebTokens` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Logging` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Protocols` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Tokens` | `7.5.0` | MIT |
| `Microsoft.SqlServer.Server` | `1.0.0` | MIT |
| `ModelContextProtocol.Core` | `1.3.0` | Apache-2.0 |
| `MySqlConnector` | `2.4.0` | MIT |
| `NodaTime` | `3.1.12` | Apache-2.0 |
| `Npgsql` | `9.0.3` | PostgreSQL |
| `Onigwrap` | `1.0.11` | MIT |
| `Oracle.ManagedDataAccess.Core` | `23.7.0` | NOASSERTION (nuspec file: `LICENSE.txt`) |
| `PDFtoImage` | `5.3.0` | MIT |
| `Porta.Pty` | `1.0.7` | MIT |
| `SMBLibrary` | `1.5.7.1` | LGPL-3.0-or-later |
| `SQLite3MC.PCLRaw.bundle` | `2.4.0` | MIT |
| `SQLite3MC.PCLRaw.lib` | `2.4.0` | MIT |
| `SQLite3MC.PCLRaw.provider` | `2.4.0` | MIT |
| `SQLitePCLRaw.core` | `3.0.2` | Apache-2.0 |
| `SSH.NET` | `2025.1.0` | MIT |
| `ShimSkiaSharp` | `5.1.1` | MIT |
| `SkiaSharp.NativeAssets.macOS` | `4.150.1` | MIT |
| `SkiaSharp` | `4.150.1` | MIT |
| `SshNet.Agent` | `2024.2.0.5` | MIT |
| `Sugiyama` | `0.12.1` | MIT |
| `Svg.Animation` | `5.1.1` | MIT |
| `Svg.Controls.Skia.Avalonia` | `12.0.0.13` | MIT |
| `Svg.Custom` | `5.1.1` | MS-PL |
| `Svg.Model` | `5.1.1` | MIT |
| `Svg.SceneGraph` | `5.1.1` | MIT |
| `Svg.Skia` | `5.1.1` | MIT |
| `System.ClientModel` | `1.0.0` | MIT |
| `System.Configuration.ConfigurationManager` | `9.0.4` | MIT |
| `System.Diagnostics.EventLog` | `9.0.4` | MIT |
| `System.Diagnostics.PerformanceCounter` | `8.0.0` | MIT |
| `System.DirectoryServices.Protocols` | `8.0.0` | MIT |
| `System.IdentityModel.Tokens.Jwt` | `7.5.0` | MIT |
| `System.Memory.Data` | `1.0.2` | MIT |
| `System.Security.Cryptography.Pkcs` | `9.0.4` | MIT |
| `System.Security.Cryptography.ProtectedData` | `10.0.10` | MIT |
| `TextMateSharp.Grammars` | `2.0.4` | MIT |
| `TextMateSharp` | `2.0.4` | MIT |
| `Tmds.DBus.Protocol` | `0.92.0` | MIT |
| `Vanara.Core` | `4.2.1` | MIT |
| `Vanara.PInvoke.Kernel32` | `4.2.1` | MIT |
| `Vanara.PInvoke.Shared` | `4.2.1` | MIT |
| `bblanchon.PDFium.macOS` | `152.0.7961` | Apache-2.0 |

## Lucide icon geometry

The Dock drop-target vectors are adapted from Lucide Icons.

ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.

This notice is informational and is not legal advice. It must not be used as
evidence that the M4 release license gate is complete.
