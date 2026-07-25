using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class TransientOverlayViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string>
        CommandPaletteInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ActivateSearchResultRequested"] = "OnLauncherSearchResultClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["SearchKeyDownRequested"] = "OnCommandSearchKeyDown",
            };

    private static readonly IReadOnlyDictionary<string, string>
        NewPanelChooserInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AddBrowserPanelRequested"] = "OnAddBrowserPanelClick",
                ["AddFilePanelRequested"] = "OnAddFilePanelClick",
                ["AddProcessMonitorPanelRequested"] =
                    "OnAddProcessMonitorPanelClick",
                ["AddStatisticsPanelRequested"] = "OnAddStatisticsPanelClick",
                ["AddTerminalPanelRequested"] = "OnAddTerminalPanelClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            };

    private static readonly IReadOnlyDictionary<string, string>
        NewItemLauncherInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AddConnectionRequested"] = "OnAddConnectionClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["CreateScreenRequested"] = "OnCreateScreenClick",
                ["CreateWorkspaceRequested"] = "OnCreateWorkspaceClick",
                ["NewBrowserRequested"] = "OnNewBrowserClick",
                ["NewFileViewerRequested"] = "OnNewFileViewerClick",
                ["NewLocalTerminalRequested"] = "OnNewLocalTerminalClick",
                ["NewProcessMonitorRequested"] = "OnNewProcessMonitorClick",
                ["NewStatisticsRequested"] = "OnNewStatisticsClick",
                ["OpenConnectionRequested"] = "OnOpenConnectionClick",
                ["OpenScreenRequested"] = "OnOpenScreenClick",
                ["OpenWorkspaceRequested"] = "OnOpenWorkspaceClick",
                ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
                ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            };

    [Fact]
    public void Main_window_delegates_three_transient_overlays_to_named_views()
    {
        var mainWindow = LoadView("MainWindow");
        AssertDelegatedOverlay(
            mainWindow,
            "CommandPaletteView",
            "CommandPaletteOverlayView",
            "{Binding IsCommandPaletteVisible}",
            CommandPaletteInteractions);
        AssertDelegatedOverlay(
            mainWindow,
            "NewItemLauncherView",
            "NewItemLauncherOverlayView",
            "{Binding IsNewItemVisible}",
            NewItemLauncherInteractions);
        AssertDelegatedOverlay(
            mainWindow,
            "NewPanelChooserView",
            "NewPanelChooserOverlayView",
            "{Binding IsNewPanelVisible}",
            NewPanelChooserInteractions);

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        Assert.Contains(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding IsLayoutDesignerVisible}",
                    StringComparison.Ordinal));

        Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "Grid"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding HasOverlay}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Background"),
                    "#F20B0B0C",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Command_palette_view_preserves_geometry_search_and_accessibility()
    {
        var commandPalette = LoadOverlay("CommandPaletteView");
        var root = Assert.IsType<XElement>(commandPalette.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("680", AttributeValue(card, "Width"));
        Assert.Equal("700", AttributeValue(card, "MaxHeight"));

        var search = FindNamedElement(root, "CommandSearchBox");
        Assert.Equal(
            "{Binding LauncherSearchQuery}",
            AttributeValue(search, "Text"));
        Assert.Equal("OnSearchKeyDown", AttributeValue(search, "KeyDown"));
        Assert.Equal(
            "Search commands and launch targets",
            AttributeValue(search, "AutomationProperties.Name"));

        var results = FindNamedElement(root, "LauncherSearchResultList");
        Assert.Equal(
            "{Binding LauncherSearchResults}",
            AttributeValue(results, "ItemsSource"));
        Assert.Equal(
            "{Binding SelectedLauncherSearchResult, Mode=TwoWay}",
            AttributeValue(results, "SelectedItem"));
        Assert.Contains(
            results.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnActivateSearchResultClick",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding IsAvailable}",
                    StringComparison.Ordinal));

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Launcher search has no results",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void New_item_launcher_preserves_geometry_catalogs_and_creation_inputs()
    {
        var launcher = LoadOverlay("NewItemLauncherView");
        var root = Assert.IsType<XElement>(launcher.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("90,54", AttributeValue(card, "Margin"));
        Assert.Equal("1120", AttributeValue(card, "MaxWidth"));
        Assert.Equal("760", AttributeValue(card, "MaxHeight"));
        Assert.Equal("Stretch", AttributeValue(card, "HorizontalAlignment"));
        Assert.Equal("Stretch", AttributeValue(card, "VerticalAlignment"));

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding NewItemLauncherTitle}",
                    StringComparison.Ordinal));

        var choices = root.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => HasClasses(
                element,
                "ChooserButton",
                "LauncherChooser"))
            .ToArray();
        Assert.Equal(5, choices.Length);

        var initialAction = FindNamedElement(root, "NewTerminalButton");
        Assert.Equal(
            "OnNewLocalTerminalClick",
            AttributeValue(initialAction, "Click"));
        Assert.Contains(
            choices,
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Open a new native browser",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding CanCreateBrowserPanel}",
                    StringComparison.Ordinal));

        foreach (var catalog in new[]
                 {
                     "{Binding Workspaces}",
                     "{Binding Connections}",
                     "{Binding Screens}",
                 })
        {
            Assert.Contains(
                root.Descendants(),
                element => element.Name.LocalName == "ItemsControl"
                    && string.Equals(
                        AttributeValue(element, "ItemsSource"),
                        catalog,
                        StringComparison.Ordinal));
        }

        Assert.Equal(
            "Workspace name",
            AttributeValue(
                FindNamedElement(root, "NewWorkspaceName"),
                "PlaceholderText"));
        Assert.Equal(
            "Saved screen name",
            AttributeValue(
                FindNamedElement(root, "NewScreenName"),
                "PlaceholderText"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && HasClasses(element, "SearchButton")
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Search commands, connections, screens, workspaces, and session history",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void New_panel_chooser_preserves_geometry_choices_and_availability()
    {
        var chooser = LoadOverlay("NewPanelChooserView");
        var root = Assert.IsType<XElement>(chooser.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("900", AttributeValue(card, "Width"));

        var choices = root.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => HasClasses(
                element,
                "ChooserButton",
                "PanelChooser"))
            .ToArray();
        Assert.Equal(5, choices.Length);

        var initialAction = FindNamedElement(root, "NewPanelTerminalButton");
        Assert.Equal(
            "OnAddTerminalPanelClick",
            AttributeValue(initialAction, "Click"));
        Assert.Contains(
            choices,
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Add native browser panel",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding CanCreateBrowserPanel}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    "Open layout designer instead",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnShowLayoutDesignerClick",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_views_forward_original_events_and_own_only_namescope_mechanics()
    {
        var commandPaletteCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class CommandPaletteView");
        AssertForwardingContract(
            commandPaletteCode,
            CommandPaletteInteractions.Keys);
        Assert.Contains("internal void FocusSearch()", commandPaletteCode);
        Assert.Contains(
            "internal void ScrollSelectedResultIntoView()",
            commandPaletteCode);
        Assert.Contains(
            "LauncherSearchResultList.ScrollIntoView(selected);",
            commandPaletteCode);

        var newItemLauncherCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class NewItemLauncherView");
        AssertForwardingContract(
            newItemLauncherCode,
            NewItemLauncherInteractions.Keys);
        Assert.Contains(
            "internal void FocusInitialAction()",
            newItemLauncherCode);
        Assert.Contains(
            "NewTerminalButton.Focus(NavigationMethod.Tab);",
            newItemLauncherCode);
        Assert.Contains(
            "internal string WorkspaceName =>",
            newItemLauncherCode);
        Assert.Contains(
            "NewWorkspaceName.Text ?? string.Empty;",
            newItemLauncherCode);
        Assert.Contains(
            "internal void ClearWorkspaceName()",
            newItemLauncherCode);
        Assert.Contains(
            "NewWorkspaceName.Text = string.Empty;",
            newItemLauncherCode);
        Assert.Contains(
            "internal string ScreenName =>",
            newItemLauncherCode);
        Assert.Contains(
            "NewScreenName.Text ?? string.Empty;",
            newItemLauncherCode);
        Assert.Contains(
            "internal void ClearScreenName()",
            newItemLauncherCode);
        Assert.Contains(
            "NewScreenName.Text = string.Empty;",
            newItemLauncherCode);

        var newPanelChooserCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class NewPanelChooserView");
        AssertForwardingContract(
            newPanelChooserCode,
            NewPanelChooserInteractions.Keys);
        Assert.Contains(
            "internal void FocusInitialAction()",
            newPanelChooserCode);
        Assert.Contains(
            "NewPanelTerminalButton.Focus(NavigationMethod.Tab);",
            newPanelChooserCode);
    }

    [Fact]
    public void Main_window_uses_typed_overlay_bridges_and_retains_effect_ownership()
    {
        var mainWindowCode = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class MainWindow");

        Assert.Contains(
            "this.FindControl<CommandPaletteView>(\"CommandPaletteOverlayView\")",
            mainWindowCode);
        Assert.Contains(
            "this.FindControl<NewItemLauncherView>(\"NewItemLauncherOverlayView\")",
            mainWindowCode);
        Assert.Contains(
            "this.FindControl<NewPanelChooserView>(\"NewPanelChooserOverlayView\")",
            mainWindowCode);
        Assert.Contains("CommandPaletteOverlay.FocusSearch();", mainWindowCode);
        Assert.Contains(
            "CommandPaletteOverlay.ScrollSelectedResultIntoView();",
            mainWindowCode);
        Assert.Contains(
            "NewItemLauncherOverlay.FocusInitialAction();",
            mainWindowCode);
        Assert.Contains(
            "NewItemLauncherOverlay.WorkspaceName,",
            mainWindowCode);
        Assert.Contains(
            "NewItemLauncherOverlay.ClearWorkspaceName();",
            mainWindowCode);
        Assert.Contains(
            "NewItemLauncherOverlay.ScreenName);",
            mainWindowCode);
        Assert.Contains(
            "NewItemLauncherOverlay.ClearScreenName();",
            mainWindowCode);
        Assert.Contains(
            "NewPanelChooserOverlay.FocusInitialAction();",
            mainWindowCode);

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain($"\"{extractedName}\"", mainWindowCode);
        }

        Assert.Contains("private async Task<bool> TryCloseOverlayAsync()", mainWindowCode);
        Assert.Contains("new DiscardChangesDialog()", mainWindowCode);
        Assert.Contains("ExecuteLauncherSearchTargetAsync(", mainWindowCode);
        Assert.Contains("ViewModel.AddLocalTerminalPanelAsync(", mainWindowCode);
        Assert.Contains("ViewModel.CreateWorkspaceAsync(", mainWindowCode);
        Assert.Contains("new SavedScreenEditorDialog(", mainWindowCode);
        Assert.Contains("FocusCurrentRoute();", mainWindowCode);
    }

    private static readonly string[] ExtractedControlNames =
    [
        "CommandSearchBox",
        "LauncherSearchResultList",
        "NewScreenName",
        "NewTerminalButton",
        "NewWorkspaceName",
        "NewPanelTerminalButton",
    ];

    private static void AssertDelegatedOverlay(
        XDocument mainWindow,
        string viewName,
        string instanceName,
        string visibilityBinding,
        IReadOnlyDictionary<string, string> interactions)
    {
        var overlay = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == viewName);
        Assert.Equal(instanceName, AttributeValue(overlay, "Name"));
        Assert.Equal(visibilityBinding, AttributeValue(overlay, "IsVisible"));

        foreach (var (interaction, handler) in interactions)
        {
            Assert.Equal(handler, AttributeValue(overlay, interaction));
        }
    }

    private static void AssertStretchingUserControl(XElement root)
    {
        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal(
            "Stretch",
            AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal(
            "Stretch",
            AttributeValue(root, "VerticalContentAlignment"));
    }

    private static XElement AssertOverlayCard(XElement root)
    {
        var card = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Border"
                && HasClasses(element, "OverlayCard"));
        Assert.Equal(
            "Cycle",
            AttributeValue(card, "KeyboardNavigation.TabNavigation"));
        return card;
    }

    private static void AssertForwardingContract(
        string codeBehind,
        IEnumerable<string> interactions)
    {
        foreach (var interaction in interactions)
        {
            Assert.Contains($" {interaction};", codeBehind);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind);
        }

        Assert.DoesNotContain("async ", codeBehind);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind);
        Assert.DoesNotContain("ShowDialog", codeBehind);
        Assert.DoesNotContain("_lifetime", codeBehind);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind);
    }

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static bool HasClasses(XElement element, params string[] classes)
    {
        var actual = (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return classes.All(expected =>
            actual.Contains(expected, StringComparer.Ordinal));
    }

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadOverlay(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Overlays",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
