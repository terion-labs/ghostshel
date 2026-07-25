using GhostShell.App.ViewModels;

namespace GhostShell.Desktop;

internal sealed class DesktopProductComponentCatalog : IProductComponentCatalog
{
    public IReadOnlyList<ProductComponentViewModel> Components { get; } =
    [
        new("Avalonia Desktop", "12.0.1", "Cross-platform desktop UI", "MIT"),
        new(
            "Avalonia WebView",
            "12.0.1",
            "Platform-native embedded browser",
            "MIT"),
        new(
            "Ghostty",
            "1.3.1",
            "Native terminal rendering and shell integration",
            "MIT + GPL-3.0-or-later resources"),
        new(".NET", "10", "Managed runtime", "MIT + bundled notices"),
        new("SQLitePCLRaw", "2.1.12", "Local durable storage", "Apache-2.0"),
        new("SSH.NET", "2025.1.0", "SSH and SFTP connectivity", "MIT"),
        new("Fluent Icons", "2.1.333", "Interface iconography", "MIT"),
    ];
}
