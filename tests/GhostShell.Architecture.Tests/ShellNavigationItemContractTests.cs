using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class ShellNavigationItemContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Launcher_and_settings_share_the_concrete_navigation_item()
    {
        Assert.Equal(5, CountItems("LauncherView"));
        Assert.Equal(11, CountItems("SettingsView"));
    }

    [Fact]
    public void Navigation_item_keeps_focus_click_state_and_accessibility_on_the_button()
    {
        var component = LoadComponent();
        var root = Assert.IsType<XElement>(component.Root);
        var button = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button");

        Assert.Equal("False", AttributeValue(root, "Focusable"));
        Assert.Equal("NavButton", AttributeValue(button, "Classes"));
        Assert.Equal(
            "{Binding IsActive, ElementName=Root}",
            AttributeValue(button, "Classes.active"));
        Assert.Equal("OnClick", AttributeValue(button, "Click"));
        Assert.Equal(
            "{Binding AutomationName, ElementName=Root}",
            AttributeValue(button, "AutomationProperties.Name"));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class ShellNavigationItem");
        Assert.Contains(
            "NavigationButton.Focus(NavigationMethod.Tab)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("Click?.Invoke(sender, e);", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
    }

    private static int CountItems(string view)
    {
        var document = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));
        return document.Descendants()
            .Count(element => element.Name.LocalName == "ShellNavigationItem");
    }

    private static XDocument LoadComponent() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            "ShellNavigationItem.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
