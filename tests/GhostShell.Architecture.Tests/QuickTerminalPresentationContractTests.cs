using System.Text.RegularExpressions;
using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class QuickTerminalPresentationContractTests
{
    [Fact]
    public void Quick_terminal_reveals_inside_a_clipped_transparent_window()
    {
        var document = XDocument.Load(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "QuickTerminalWindow.axaml"));
        var root = Assert.IsType<XElement>(document.Root);
        var viewport = Assert.Single(
            root.Descendants(),
            element => AttributeValue(element, "Name") == "RevealViewport");
        var slidingPanel = Assert.Single(
            root.Descendants(),
            element => AttributeValue(element, "Name") == "SlidingPanel");

        Assert.Equal("Transparent", AttributeValue(root, "Background"));
        Assert.Equal("True", AttributeValue(viewport, "ClipToBounds"));
        Assert.Equal("Transparent", AttributeValue(viewport, "Background"));
        Assert.Equal("Transparent", AttributeValue(slidingPanel, "Background"));
    }

    [Fact]
    public void Quick_terminal_native_window_is_placed_once_and_never_animated_off_screen()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var controller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "QuickTerminalController.cs"));
        var window = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));
        var toggle = Regex.Match(
            controller,
            @"public void Toggle\(\)(?<body>.*?)public void Hide\(\)",
            RegexOptions.Singleline);

        Assert.True(toggle.Success);
        Assert.Single(Regex.Matches(controller, @"window\.Position\s*="));
        Assert.DoesNotContain("AboveWorkingArea", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimatePosition", controller, StringComparison.Ordinal);
        Assert.Contains("AnimateReveal", controller, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplySettings",
            toggle.Groups["body"].Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "visual.StartAnimation(\"Translation\", animation)",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowTransparencyLevel.None",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_does_not_republish_an_unchanged_native_transparency_hint()
    {
        var window = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));

        Assert.Contains(
            "if (TransparencyLevelHint.SequenceEqual(hint))",
            window,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            window,
            @"TransparencyLevelHint\s*=\s*hint;"));
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;
}
