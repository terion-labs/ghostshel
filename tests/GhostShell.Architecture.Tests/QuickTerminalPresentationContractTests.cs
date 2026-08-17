using System.Text.RegularExpressions;
using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class QuickTerminalPresentationContractTests
{
    [Fact]
    public void Main_window_reconciles_macos_backing_scale_after_display_changes()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var window = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "MainWindow.axaml.cs"));
        var backingScale = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "MacOsWindowBackingScale.cs"));

        Assert.Contains("ScalingChanged += OnWindowScalingChanged", window, StringComparison.Ordinal);
        Assert.Contains("Screens.Changed += OnScreensChanged", window, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(750)", window, StringComparison.Ordinal);
        Assert.Contains("QueueBackingScaleReconciliation();", window, StringComparison.Ordinal);
        Assert.Contains("viewDidChangeBackingProperties", backingScale, StringComparison.Ordinal);
        Assert.Contains("intermediate pixel size", backingScale, StringComparison.Ordinal);
    }

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
        Assert.Null(AttributeValue(root, "TransparencyBackgroundFallback"));
        Assert.Equal("True", AttributeValue(viewport, "ClipToBounds"));
        Assert.Equal("Transparent", AttributeValue(viewport, "Background"));
        Assert.Equal("Transparent", AttributeValue(slidingPanel, "Background"));
        Assert.Equal("0", AttributeValue(slidingPanel, "CornerRadius"));
        Assert.Contains(
            root.Descendants(),
            element => AttributeValue(element, "Name") == "ResizeGrip"
                && AttributeValue(element, "PointerPressed") == "OnResizeGripPointerPressed");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "PanelConnectionSelectorView"
                && AttributeValue(element, "Options") == "{Binding ConnectionOptions}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "RuntimeTabStripView"
                && AttributeValue(element, "Tabs") == "{Binding Tabs}"
                && AttributeValue(element, "IconPickerPlacement") == "TopEdgeAlignedLeft"
                && AttributeValue(element, "AddTabRequested") == "OnAddTabRequested");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "Classes") == "PanelAgentGlow"
                && AttributeValue(element, "BoxShadow")
                    == "{DynamicResource ShellAgentPanelGlowShadow}"
                && AttributeValue(element, "IsVisible")
                    == "{Binding ActiveTab.HasAgentActivity}");
        var agentGlow = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "Classes") == "PanelAgentGlow");
        Assert.Null(AttributeValue(agentGlow, "Margin"));
        Assert.Equal("0", AttributeValue(agentGlow, "CornerRadius"));
        var controlBar = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Grid"
                && AttributeValue(element, "ColumnDefinitions") == "*,Auto,Auto"
                && element.Descendants().Any(descendant =>
                    AttributeValue(descendant, "Click") == "OnHideClick"));
        Assert.Equal(
            "{controls:Inset Left=Md, Right=Xs}",
            AttributeValue(controlBar, "Margin"));
        var statusBackground = Assert.Single(
            root.Descendants(),
            element => AttributeValue(element, "Name")
                == "QuickTerminalStatusBackground");
        Assert.Equal(
            "{DynamicResource ShellSurfaceBrush}",
            AttributeValue(statusBackground, "Background"));
        var statusDivider = Assert.Single(
            root.Descendants(),
            element => AttributeValue(element, "Name")
                == "QuickTerminalStatusDivider");
        Assert.Equal("1", AttributeValue(statusDivider, "Height"));
        Assert.Equal("Top", AttributeValue(statusDivider, "VerticalAlignment"));
        Assert.Equal(
            "{DynamicResource ShellBorderBrush}",
            AttributeValue(statusDivider, "Background"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "AgentWorkspaceView"
                && AttributeValue(element, "Name") == "QuickTerminalAgentSurface"
                && AttributeValue(element, "MaxHeight")
                    == "{Binding #AgentViewport.Bounds.Height}"
                && AttributeValue(element, "Classes.floating")
                    == "{Binding !IsAgentPanelDocked}"
                && AttributeValue(element, "Classes") == "edgeResizable"
                && AttributeValue(element, "Classes.docked")
                    == "{Binding IsAgentPanelDocked}"
                && AttributeValue(element, "Classes.edgeLeft")
                    == "{Binding IsAgentPanelOnLeft}"
                && AttributeValue(element, "Classes.edgeRight")
                    == "{Binding IsAgentPanelOnRight}"
                && AttributeValue(element, "Classes.anchorBottom")
                    == "{Binding IsAgentPanelAnchoredBottom}"
                && AttributeValue(element, "Classes.anchorTop")
                    == "{Binding IsAgentPanelAnchoredTop}"
                && AttributeValue(element, "HorizontalAlignment")
                    == "{Binding AgentPanelAlignment}"
                && AttributeValue(element, "VerticalAlignment")
                    == "{Binding AgentPanelVerticalAlignment}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "Width")
                    == "{Binding #QuickTerminalAgentSurface.Bounds.Width}"
                && AttributeValue(element, "IsVisible")
                    == "{Binding IsAgentPanelDockedVisible}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Click") == "OnToggleAgentClick"
                && element.Descendants().Any(descendant =>
                    AttributeValue(descendant, "Symbol") == "Bot"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "SymbolIcon"
                && AttributeValue(element, "Classes")
                    == "AgentToolbarActivityPulse"
                && AttributeValue(element, "Classes.running")
                    == "{Binding AgentChat.IsBusy}"
                && AttributeValue(element, "Opacity") == "0");
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "Click") == "OnSettingsClick");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Setter"
                && AttributeValue(element, "Property") == "CornerRadius"
                && AttributeValue(element, "Value") == "0"
                && element.Parent is { } style
                && AttributeValue(style, "Selector")
                    == "views|AgentWorkspaceView.docked Border.AgentPanel");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Setter"
                && AttributeValue(element, "Property") == "BorderThickness"
                && AttributeValue(element, "Value") == "1,0,1,0"
                && element.Parent is { } style
                && AttributeValue(style, "Selector")
                    == "views|AgentWorkspaceView.docked Border.AgentPanel");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "views|AgentWorkspaceView.docked.nativeMaterial Border.AgentPanel"
                && element.Elements().Any(setter =>
                    AttributeValue(setter, "Property") == "Background"
                    && AttributeValue(setter, "Value") == "Transparent"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "views|AgentWorkspaceView.floating.anchorBottom"
                && element.Elements().Any(setter =>
                    AttributeValue(setter, "Property") == "Margin"
                    && AttributeValue(setter, "Value") == "0,12,0,0"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "views|AgentWorkspaceView.floating.anchorTop"
                && element.Elements().Any(setter =>
                    AttributeValue(setter, "Property") == "Margin"
                    && AttributeValue(setter, "Value") == "0,0,0,12"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "Text") is
                "{Binding ProfileName, StringFormat={}Profile · {0}}"
                or "{Binding ShortcutStatus}"
                or "{Binding EscapeStatus}");
    }

    [Fact]
    public void Quick_terminal_moves_one_fixed_size_native_surface()
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
        Assert.Contains("window.PlaceAt(workingArea.Position, scale)", controller, StringComparison.Ordinal);
        Assert.Contains("CompletePreparedReveal", controller, StringComparison.Ordinal);
        Assert.Contains("AnimateReveal", controller, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplySettings",
            toggle.Groups["body"].Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryAnimate",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryClearWindowBacking",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryKeepBackdropActive",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsWindowMaterial.TrySit",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsMaterial.HudWindow",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TrySetChromeMaterial",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TrySetAgentMaterial",
            window,
            StringComparison.Ordinal);
        Assert.Contains("MacOsMaterial.Sidebar", window, StringComparison.Ordinal);
        var nativeReveal = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "MacOsQuickTerminalReveal.cs"));
        Assert.Contains("SetRevealFrames", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("NSVisualEffectView", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("setState:", nativeReveal, StringComparison.Ordinal);
        Assert.Contains(
            "GhostShellQuickTerminalChromeView",
            nativeReveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "GhostShellQuickTerminalAgentView",
            nativeReveal,
            StringComparison.Ordinal);
        Assert.Contains("objc_allocateClassPair", nativeReveal, StringComparison.Ordinal);
        Assert.DoesNotContain("setTag:", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("MacOsMaterial.HudWindow", window, StringComparison.Ordinal);
        Assert.Contains("AvnView", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("MacOsQuickTerminalFocus.CaptureFrontmostApplication", controller, StringComparison.Ordinal);
        Assert.Contains("MacOsQuickTerminalFocus.TryRestoreFrontmostApplication", controller, StringComparison.Ordinal);
        Assert.Contains("PositionForProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("HeightForProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementComposition", window, StringComparison.Ordinal);
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

    [Fact]
    public void Quick_terminal_forced_close_is_safe_when_shutdown_reenters()
    {
        var window = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));
        var closing = Regex.Match(
            window,
            @"private void OnWindowClosing\(.*?private void RequestDismiss",
            RegexOptions.Singleline);

        Assert.True(closing.Success);
        Assert.Contains("if (_allowClose)", closing.Value, StringComparison.Ordinal);
        Assert.Contains("_lifetime.Cancel()", closing.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose()", closing.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_new_tab_command_targets_the_active_quick_terminal()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var application = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml.cs"));
        var controller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GhostShell.App",
            "QuickTerminalController.cs"));

        Assert.Contains(
            "await QuickTerminalController.TryAddTabToActiveQuickTerminalAsync()",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_quickWindowIsActive || _quickWindow?.IsVisible != true)",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("await _viewModel.AddTabAsync()", controller, StringComparison.Ordinal);
        Assert.Contains("_quickWindow.FocusTerminal()", controller, StringComparison.Ordinal);
        Assert.Contains(
            "await QuickTerminalController.TryCloseTabInActiveQuickTerminalAsync()",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _viewModel.CloseTabAsync(activeTab)",
            controller,
            StringComparison.Ordinal);
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;
}
