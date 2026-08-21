using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class TerminalNotificationProtocolContractTests
{
    private static readonly string RepositoryRoot =
        ApplicationViewCatalog.Load().RepositoryRoot;

    [Fact]
    public void Terminal_notification_ingress_does_not_rewrite_application_launches()
    {
        var factory = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Terminal",
            "GhosttyVtTerminalSessionFactory.cs"));

        Assert.Contains("shellIntegration.Launch", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationAdapter", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_package_contains_no_provider_notification_plugins_or_shims()
    {
        Assert.False(Directory.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "Resources",
            "Claude")));

        var packageInputs = new[]
        {
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"),
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"),
            Path.Combine(
                RepositoryRoot,
                "tools",
                "GhostShell.Packaging",
                "MacOsAppBundleBuilder.cs"),
        };
        var forbiddenPaths = new[]
        {
            "claude-plugins",
            "ghostshell-cli-shims",
            "terminal-shell-integration",
        };

        foreach (var input in packageInputs)
        {
            var contents = File.ReadAllText(input);
            foreach (var forbiddenPath in forbiddenPaths)
            {
                Assert.DoesNotContain(
                    forbiddenPath,
                    contents,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
