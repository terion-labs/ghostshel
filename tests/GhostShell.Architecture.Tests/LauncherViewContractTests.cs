using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class LauncherViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> ShellInteractions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AddConnectionRequested"] = "OnAddConnectionClick",
            ["CancelHistoryExportRequested"] = "OnCancelHistoryExportClick",
            ["ClearRecentSessionsRequested"] = "OnClearRecentSessionsClick",
            ["DeleteConnectionRequested"] = "OnDeleteConnectionClick",
            ["EditConnectionRequested"] = "OnEditConnectionClick",
            ["EditScreenRequested"] = "OnEditScreenClick",
            ["ExportAllHistoryRequested"] = "OnExportAllHistoryClick",
            ["ExportFilteredHistoryRequested"] = "OnExportFilteredHistoryClick",
            ["FinishOnboardingRequested"] = "OnFinishOnboardingClick",
            ["HistorySearchKeyDownRequested"] = "OnHistorySearchKeyDown",
            ["ImportDefinitionsRequested"] = "OnImportDefinitionsClick",
            ["LauncherConnectionsRequested"] = "OnLauncherConnectionsClick",
            ["LauncherHistoryRequested"] = "OnLauncherHistoryClick",
            ["LauncherHomeRequested"] = "OnLauncherHomeClick",
            ["LauncherScreensRequested"] = "OnLauncherScreensClick",
            ["OpenConnectionRequested"] = "OnOpenConnectionClick",
            ["OpenRecentSessionRequested"] = "OnOpenRecentSessionClick",
            ["OpenScreenRequested"] = "OnOpenScreenClick",
            ["OpenSelectedHistorySessionRequested"] = "OnOpenSelectedHistorySessionClick",
            ["ResetRecentSessionHistoryRequested"] = "OnResetRecentSessionHistoryClick",
            ["RetryOnboardingRequested"] = "OnRetryOnboardingClick",
            ["RetryRecentSessionHistoryRequested"] = "OnRetryRecentSessionHistoryClick",
            ["ReviewHistoryPrivacyRequested"] = "OnReviewHistoryPrivacyClick",
            ["SaveHistoryRetentionRequested"] = "OnSaveHistoryRetentionClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["ShowSettingsRequested"] = "OnShowSettingsClick",
        };

    [Fact]
    public void Main_window_delegates_the_launcher_route_to_one_named_view()
    {
        var mainWindow = LoadView("MainWindow");
        var launcher = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "LauncherView");

        Assert.Equal("LauncherRouteView", AttributeValue(launcher, "Name"));
        Assert.Equal(
            "{Binding IsLauncherVisible}",
            AttributeValue(launcher, "IsVisible"));

        foreach (var (interaction, handler) in ShellInteractions)
        {
            Assert.Equal(handler, AttributeValue(launcher, interaction));
        }

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Launcher_view_preserves_route_structure_state_and_accessibility()
    {
        var launcher = LoadView("LauncherView");
        var root = Assert.IsType<XElement>(launcher.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        var home = FindNamedElement(root, "LauncherHomeButton");
        Assert.Equal("Launcher home", AttributeValue(home, "AutomationProperties.Name"));
        Assert.Equal("OnLauncherHomeClick", AttributeValue(home, "Click"));
        Assert.Equal(
            "{Binding IsLauncherOverviewVisible}",
            AttributeValue(home, "Classes.active"));

        var historySearch = FindNamedElement(root, "HistorySearchBox");
        Assert.Equal(
            "Search session history",
            AttributeValue(historySearch, "AutomationProperties.Name"));
        Assert.Equal("OnHistorySearchKeyDown", AttributeValue(historySearch, "KeyDown"));
        Assert.Equal(
            "{Binding HistorySearchQuery}",
            AttributeValue(historySearch, "Text"));

        Assert.Equal(
            "Polite",
            AttributeValue(
                FindUniqueAccessibleElement(root, "Getting started status"),
                "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Polite",
            AttributeValue(
                FindUniqueAccessibleElement(root, "Session history has no results"),
                "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Polite",
            AttributeValue(
                FindUniqueAccessibleElement(root, "History retention status"),
                "AutomationProperties.LiveSetting"));

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding Connections}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding Screens}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding RecentSessions}",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Launcher_view_forwards_leaf_interactions_without_taking_shell_ownership()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class LauncherView");

        foreach (var interaction in ShellInteractions.Keys)
        {
            Assert.Contains(
                $"? {interaction};",
                codeBehind,
                StringComparison.Ordinal);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_uses_typed_launcher_focus_apis_across_the_namescope()
    {
        var mainWindowCode = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class MainWindow");

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                $"\"{extractedName}\"",
                mainWindowCode,
                StringComparison.Ordinal);
        }

        Assert.Contains("FocusHomeNavigation()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("FocusHistoryNavigation()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("FocusHistorySearch()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("FocusOnboardingFinish()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("FocusOverviewSection(section, resetScroll)", mainWindowCode, StringComparison.Ordinal);
    }

    private static readonly string[] ExtractedControlNames =
    [
        "LauncherHomeButton",
        "LauncherHistoryButton",
        "LauncherScrollViewer",
        "LauncherHomeSection",
        "LauncherOnboardingCard",
        "OnboardingFinishButton",
        "LauncherConnectionsSection",
        "LauncherScreensSection",
        "LauncherHistorySection",
        "HistorySearchBox",
        "HistorySessionList",
    ];

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static XElement FindUniqueAccessibleElement(
        XElement root,
        string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
