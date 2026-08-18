using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class NotificationPresentationContractTests
{
    private static readonly string RepositoryRoot =
        ApplicationViewCatalog.Load().RepositoryRoot;

    [Fact]
    public void Every_runtime_panel_routes_its_unread_state_into_shared_chrome()
    {
        var viewsDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "RuntimePanels");
        var panelViews = Directory
            .EnumerateFiles(viewsDirectory, "*PanelView.axaml")
            .Select(XDocument.Load)
            .Select(document => Assert.IsType<XElement>(document.Root))
            .Select(root => root.DescendantsAndSelf()
                .FirstOrDefault(element => element.Name.LocalName == "PanelChrome"))
            .OfType<XElement>()
            .ToArray();

        Assert.NotEmpty(panelViews);
        Assert.All(
            panelViews,
            chrome =>
            {
                Assert.Equal(
                    "{Binding HasAttention}",
                    AttributeValue(chrome, "HasAttention"));
                Assert.Equal(
                    "{Binding IsNotificationPulseActive}",
                    AttributeValue(chrome, "IsNotificationPulseActive"));
            });
    }

    [Fact]
    public void Shared_panel_chrome_draws_the_exact_source_indicator()
    {
        var designSystem = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "DesignSystem.axaml"));

        Assert.Contains(
            designSystem.Descendants(),
            element => element.Name.LocalName == "SignalDot"
                && AttributeValue(element, "IsVisible")
                    == "{TemplateBinding HasAttention}");
        Assert.Contains(
            designSystem.Descendants(),
            element => AttributeValue(element, "Name")
                    == "PART_NotificationPulse"
                && AttributeValue(element, "IsVisible") == "False");
        Assert.Contains(
            designSystem.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "^:notification-pulse /template/ Border#PART_NotificationPulse");
    }

    [Fact]
    public void Attention_indicators_use_the_legible_shared_size_token()
    {
        var applicationResources = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml.cs"));

        Assert.Contains(
            "Publish(\"ShellSignalDotSize\", Math.Round(resources.ControlMinHeight * 0.42));",
            applicationResources,
            StringComparison.Ordinal);
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
