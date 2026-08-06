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
            ["ActivateTabRequested"] = "OnActivateTabClick",
            ["CancelHistoryExportRequested"] = "OnCancelHistoryExportClick",
            ["ClearRecentSessionsRequested"] = "OnClearRecentSessionsClick",
            ["CloseRuntimeTabRequested"] = "OnCloseRuntimeTabClick",
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
            ["TitleBarPointerPressedRequested"] = "OnTitleBarPointerPressed",
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
        AssertTitleBarDragRegion(root);
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "Classes"),
                    "FloatingSidebar",
                    StringComparison.Ordinal)
                // A Border, not a subclass of one: Avalonia's type selectors
                // match one type exactly, so Border.FloatingSidebar stops
                // dressing this the moment it stops being a Border, and a
                // sidebar with no background reads as a missing sidebar.
                && string.Equals(
                    AttributeValue(element, "Concentric.IsEnabled"),
                    "True",
                    StringComparison.Ordinal));

        var tabs = FindNamedElement(root, "LauncherTabStrip");
        Assert.Equal("RuntimeTabStripView", tabs.Name.LocalName);
        Assert.Equal(
            "{Binding RuntimeWorkspace.Tabs}",
            AttributeValue(tabs, "Tabs"));
        Assert.Equal("True", AttributeValue(tabs, "ShowHomeTab"));
        Assert.Equal("True", AttributeValue(tabs, "IsHomeActive"));
        Assert.Equal("OnLauncherHomeClick", AttributeValue(tabs, "HomeRequested"));
        Assert.Equal("OnActivateTabClick", AttributeValue(tabs, "ActivateRequested"));
        Assert.Equal("OnCloseRuntimeTabClick", AttributeValue(tabs, "CloseRequested"));

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
        Assert.Equal("ShellNavigationItem", home.Name.LocalName);
        Assert.Equal("Launcher home", AttributeValue(home, "AutomationName"));
        Assert.Equal("OnLauncherHomeClick", AttributeValue(home, "Click"));
        Assert.Equal(
            "{Binding IsLauncherOverviewVisible}",
            AttributeValue(home, "IsActive"));

        foreach (var (name, automationName) in new[]
                 {
                     ("LauncherHomeSection", "Home overview"),
                     ("LauncherConnectionsSection", "Saved connections section"),
                     ("LauncherScreensSection", "Saved screens section"),
                 })
        {
            var overviewTarget = FindNamedElement(root, name);
            Assert.Equal("True", AttributeValue(overviewTarget, "Focusable"));
            Assert.Equal(
                automationName,
                AttributeValue(overviewTarget, "AutomationProperties.Name"));
        }

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
    public void Home_runtime_tab_is_the_icon_only_predefined_tab()
    {
        var tabStrip = LoadComponent("RuntimeTabStripView");
        var root = Assert.IsType<XElement>(tabStrip.Root);
        Assert.Equal(
            "{DynamicResource ShellTabStripTopClearance}",
            AttributeValue(root, "Margin"));
        var home = FindNamedElement(root, "HomeTabButton");

        // A square as tall as the tabs beside it, whatever that is. The number
        // used to be written here as well, which made every change to the
        // strip's density a test edit and said nothing about what Home is.
        var tab = Assert.Single(
            root.Descendants(),
            element => AttributeValue(element, "Classes") == "RuntimeTabDropTarget");
        var tabHeight = AttributeValue(tab, "Height");
        Assert.False(string.IsNullOrWhiteSpace(tabHeight));
        Assert.Equal(tabHeight, AttributeValue(home, "Width"));
        Assert.Equal(tabHeight, AttributeValue(home, "Height"));
        Assert.Equal("0", AttributeValue(home, "Padding"));
        Assert.Equal(
            "Open Home tab",
            AttributeValue(home, "AutomationProperties.Name"));
        Assert.Single(
            home.Elements(),
            element => element.Name.LocalName == "SymbolIcon"
                && string.Equals(
                    AttributeValue(element, "Symbol"),
                    "Home",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            home.Descendants(),
            element => element.Name.LocalName == "TextBlock");
    }

    /// <summary>
    /// A saved target is one row, whichever way it can be opened. Listing it
    /// once per supported adapter said the same host four times and buried the
    /// ordinary case; the chevron carries the rest, and appears only where
    /// there are any.
    /// </summary>
    [Fact]
    public void A_saved_connection_is_one_row_with_its_other_adapters_behind_a_chevron()
    {
        var shortcut = LoadComponent("SavedConnectionShortcutView");
        var root = Assert.IsType<XElement>(shortcut.Root);
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "SplitButton");

        var buttons = root.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        var row = Assert.Single(
            buttons,
            element => AttributeValue(element, "Classes") == "ListRow ChooserListRow");
        Assert.Equal("OnPrimaryClick", AttributeValue(row, "Click"));

        var chevron = Assert.Single(
            buttons,
            element => AttributeValue(element, "Name") == "ShortcutMenuButton");
        Assert.Equal("{Binding HasAlternatives}", AttributeValue(chevron, "IsVisible"));

        var flyout = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Flyout");
        Assert.Equal(
            "SavedConnectionMenu",
            AttributeValue(flyout, "FlyoutPresenterClasses"));
        Assert.DoesNotContain(
            flyout.Elements(),
            element => element.Name.LocalName == "Border");
        Assert.All(
            flyout.Descendants().Where(element => element.Name.LocalName == "Button"),
            element => Assert.Equal(
                "FlyoutMenuItem",
                AttributeValue(element, "Classes")));
    }

    /// <summary>
    /// The chooser is the one place a saved connection is opened from. A second
    /// strip above the workspace offered the same targets in a second shape,
    /// and every capability had to be taught to both.
    /// </summary>
    [Fact]
    public void The_chooser_is_the_only_place_saved_connections_are_launched_from()
    {
        var chooser = LoadComponent("NewItemChooserView");
        Assert.Single(
            Assert.IsType<XElement>(chooser.Root).Descendants(),
            element => element.Name.LocalName == "SavedConnectionShortcutView");

        Assert.DoesNotContain(
            Assert.IsType<XElement>(LoadView("WorkspaceView").Root).Descendants(),
            element => element.Name.LocalName == "SavedConnectionShortcutView");
    }

    [Fact]
    public void Dropdown_surfaces_share_the_saved_connection_menu_treatment()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellTheme.axaml"));

        foreach (var selector in new[]
                 {
                     "FlyoutPresenter",
                     "MenuFlyoutPresenter",
                     "ComboBox:dropdownopen /template/ Border#PopupBorder",
                     "MenuItem",
                     "ComboBoxItem",
                 })
        {
            Assert.Single(
                theme.Descendants(),
                element => element.Name.LocalName == "Style"
                    && string.Equals(
                        AttributeValue(element, "Selector"),
                        selector,
                        StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Floating_sidebars_use_their_dedicated_surface_token()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellTheme.axaml"));
        var sidebarStyle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Border.FloatingSidebar",
                    StringComparison.Ordinal));

        AssertStyleSetter(
            sidebarStyle,
            "Background",
            "{DynamicResource ShellSidebarBrush}");
        AssertStyleSetter(
            sidebarStyle,
            "BorderBrush",
            "{DynamicResource ShellSidebarBorderBrush}");
        AssertStyleSetter(sidebarStyle, "BorderThickness", "1");
        AssertStyleSetter(
            sidebarStyle,
            "CornerRadius",
            "{DynamicResource ShellSidebarCornerRadius}");

        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml"));
        foreach (var resource in new[]
                 {
                     "ShellSidebarBorderBrush",
                     "ShellSidebarSelectionBrush",
                     "ShellSidebarCornerRadius",
                 })
        {
            Assert.Contains(
                app.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Key"),
                    resource,
                    StringComparison.Ordinal));
        }
    }

    private static void AssertTitleBarDragRegion(XElement root)
    {
        var titleBar = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Classes"),
                "TopChrome",
                StringComparison.Ordinal));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Equal(
            "OnTitleBarPointerPressed",
            AttributeValue(titleBar, "PointerPressed"));
        Assert.Equal(
            "{Binding $parent[Window].TitleBarChromeHeight}",
            AttributeValue(titleBar, "MinHeight"));
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
    public void Main_window_owns_the_titlebar_move_fallback()
    {
        var mainWindow = Assert.IsType<XElement>(LoadView("MainWindow").Root);
        // The window keeps its system decorations — its frame, its rounded
        // corners, its resize edges and its standard buttons. Dropping them to
        // be rid of the title bar took all of that with it, which is a far
        // bigger loss than the band it removed; the band is dealt with by
        // letting the content run under it instead.
        Assert.Equal("Full", AttributeValue(mainWindow, "WindowDecorations"));
        Assert.Equal(
            "True",
            AttributeValue(mainWindow, "ExtendClientAreaToDecorationsHint"));
        Assert.Equal(
            "-1",
            AttributeValue(mainWindow, "ExtendClientAreaTitleBarHeightHint"));

        // Extending under the decorations is only half of it. Avalonia draws
        // its own title bar and still fills it with an opaque brush; 12.0.5
        // only moved that fill behind a named resource. Without an answer to
        // it the pale strip comes straight back, which it did once already.
        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml"));
        var titleBarFill = Assert.Single(
            app.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "TitleBarBackgroundBrush",
                StringComparison.Ordinal));
        Assert.Equal("Transparent", AttributeValue(titleBarFill, "Color"));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class MainWindow : Window");

        // Extending under the decorations is only half of it on macOS: the
        // same switch that extends the client area also tells Avalonia's
        // content view to show a title-bar material and a separator, and
        // there is no managed way to ask for one without the other. Left
        // alone they paint behind the translucent base and read as a title
        // bar. The window has to put them back to sleep, and again whenever
        // the decorations are rebuilt.
        Assert.Contains(
            "MacOsWindowTitleBar.TryLetTheBaseSurfaceRunToTheTop(",
            codeBehind,
            StringComparison.Ordinal);
        // The corners the platform draws are decided by which kind of window
        // it is asked to be, so the ask travels with the corner setting.
        Assert.Contains("MacOsWindowKind.Toolbar", codeBehind, StringComparison.Ordinal);

        Assert.Contains("TitleBarChromeHeightProperty", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "WindowTitleBarContentMarginProperty",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("RefreshWindowChromeMetrics();", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "private void OnTitleBarPointerPressed(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("e.Pointer.IsPrimary", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BeginMoveDrag(e);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_uses_typed_launcher_focus_apis_across_the_namescope()
    {
        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");

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

    [Fact]
    public void Launcher_collection_counts_use_a_shared_noninteractive_component()
    {
        var launcher = LoadView("LauncherView");
        var root = Assert.IsType<XElement>(launcher.Root);
        var countPills = root.Descendants()
            .Where(element => element.Name.LocalName == "CountPill")
            .ToArray();

        // The same count appears on Home and again on its dedicated page, so the
        // assertion is on which counts exist rather than how many pills render
        // them — a new page reusing a count is fine, a new kind of count is the
        // thing worth catching.
        Assert.Equal(
            new[] { "{Binding HistoryResultCount}", "{Binding Screens.Count}", "{Binding TotalConnectionCount}" },
            countPills
                .Select(element => AttributeValue(element, "Value") ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var countPill = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            "CountPill.axaml"));
        var countRoot = Assert.IsType<XElement>(countPill.Root);
        Assert.Equal("False", AttributeValue(countRoot, "Focusable"));
        Assert.Equal("False", AttributeValue(countRoot, "IsHitTestVisible"));
    }

    [Fact]
    public void Launcher_card_hover_surface_stretches_across_the_full_card()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellTheme.axaml"));
        var cardSurfaceStyle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.CardSurface",
                    StringComparison.Ordinal));

        AssertStyleSetter(cardSurfaceStyle, "HorizontalAlignment", "Stretch");
        AssertStyleSetter(cardSurfaceStyle, "HorizontalContentAlignment", "Stretch");
    }

    [Fact]
    public void Pill_components_share_a_smaller_scalable_font_token()
    {
        var designSystem = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "DesignSystem.axaml"));
        var statusChipTheme = Assert.Single(
            designSystem.Descendants(),
            element => element.Name.LocalName == "ControlTheme"
                && string.Equals(
                    AttributeValue(element, "TargetType"),
                    "controls:StatusChip",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            statusChipTheme,
            "FontSize",
            "{DynamicResource ShellPillFontSize}");

        var shellTheme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellTheme.axaml"));
        var interactiveChipStyle = Assert.Single(
            shellTheme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.Chip, ComboBox.Chip",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            interactiveChipStyle,
            "FontSize",
            "{DynamicResource ShellPillFontSize}");

        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml"));
        Assert.Contains(
            app.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "ShellPillFontSize",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Navigation_style_stretches_items_and_exposes_selection_state()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellTheme.axaml"));
        var navigationStyle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            navigationStyle,
            "HorizontalAlignment",
            "Stretch");
        AssertStyleSetter(
            navigationStyle,
            "Foreground",
            "{DynamicResource ShellTextBrush}");
        AssertStyleSetter(navigationStyle, "MinHeight", "32");
        AssertStyleSetter(navigationStyle, "Padding", "10,0");
        AssertStyleSetter(
            navigationStyle,
            "AutomationProperties.ItemStatus",
            string.Empty);

        var activeNavigationStyle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton.active",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            activeNavigationStyle,
            "Background",
            "{DynamicResource ShellSidebarSelectionBrush}");
        AssertStyleSetter(
            activeNavigationStyle,
            "Foreground",
            "{DynamicResource ShellAccentBrush}");
        AssertStyleSetter(activeNavigationStyle, "FontWeight", "Medium");
        AssertStyleSetter(
            activeNavigationStyle,
            "AutomationProperties.ItemStatus",
            "Current page");

        var activeLabelStyle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton.active TextBlock",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            activeLabelStyle,
            "Foreground",
            "{DynamicResource ShellAccentBrush}");
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

    private static void AssertStyleSetter(
        XElement style,
        string property,
        string value) =>
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && string.Equals(
                    AttributeValue(element, "Property"),
                    property,
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Value"),
                    value,
                    StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
