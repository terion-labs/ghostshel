using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed partial class RepositoryConventionTests
{
    [Fact]
    public void Macos_packaging_opts_the_apphost_into_current_native_chrome()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var declarationScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "declare-macos-sdk26.sh"));
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));

        Assert.Contains(
            "-set-build-version macos \"${minimum_macos}\" 26.0",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "codesign --force --sign -",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "declared_sdk",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not declare macOS SDK 26.0",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "declare-macos-sdk26.sh",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterTargets=\"Build\"",
            desktopProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_packaging_keeps_vendored_project_versions_independent()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var buildProperties = File.ReadAllText(
            Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains(
            "-p:GhostShellProductVersion=\"${version}\"",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:GhostShellCefRuntimeArtifactDirectory=\"${cef_runtime_root}\"",
            packageScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Version=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:AssemblyVersion=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:FileVersion=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:InformationalVersion=", packageScript, StringComparison.Ordinal);
        Assert.Contains(
            "StartsWith('GhostShell')",
            buildProperties,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Version>$(GhostShellProductVersion)</Version>",
            buildProperties,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Full_macos_packaging_stays_arm64_until_x64_evidence_exists()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var packagingGuide = File.ReadAllText(
            Path.Combine(RepositoryRoot, "docs", "macos-packaging.md"));

        Assert.Contains(
            "[--runtime-identifier osx-arm64]",
            packageScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "osx-arm64|osx-x64",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "currently supports only osx-arm64",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Full Intel application packaging fails fast",
            packagingGuide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_packaging_path_leak_checks_cover_merged_first_party_assemblies()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));

        Assert.Contains("\"GhostShell.Databases.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Docking.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Previews.dll\"", packageScript, StringComparison.Ordinal);
    }

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void ProductSourceUsesPanelTerminology()
    {
        var productFiles = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .Append(Path.Combine(RepositoryRoot, "README.md"));

        foreach (var file in productFiles)
        {
            var source = File.ReadAllText(file);
            Assert.False(
                PaneTerminology().IsMatch(source),
                $"{Path.GetRelativePath(RepositoryRoot, file)} uses tmux's pane terminology; GhostSHELL calls this a panel.");
        }
    }

    [Fact]
    public void ArchitectureDecisionNumbersAreUniqueAndMatchTheirHeadings()
    {
        var decisions = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "docs", "adr"), "*.md")
            .Select(path => new
            {
                Path = path,
                Number = Path.GetFileName(path).Split('-', 2)[0],
                Heading = File.ReadLines(path).FirstOrDefault() ?? string.Empty,
            })
            .ToArray();

        Assert.Equal(
            decisions.Length,
            decisions.Select(decision => decision.Number).Distinct(StringComparer.Ordinal).Count());
        Assert.All(decisions, decision => Assert.StartsWith(
            $"# ADR {decision.Number}:",
            decision.Heading,
            StringComparison.Ordinal));
    }

    [Fact]
    public void VisibleApplicationTextUsesScalableFontResources()
    {
        var applicationRoot = Path.Combine(RepositoryRoot, "src", "GhostShell.App");
        var xamlFiles = Directory
            .EnumerateFiles(applicationRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

        foreach (var file in xamlFiles)
        {
            var source = File.ReadAllText(file);
            var literalElements = LiteralElementFontSize()
                .Matches(source)
                .Select(match => match.Groups["element"].Value)
                .Where(element => !string.Equals(
                    element,
                    "icons:SymbolIcon",
                    StringComparison.Ordinal))
                .ToArray();

            Assert.True(
                literalElements.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains literal font sizes on: " +
                string.Join(", ", literalElements));
            Assert.False(
                LiteralFontSizeSetter().IsMatch(source),
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains a literal font-size style; use a ShellFontSize dynamic resource.");
        }
    }

    [Fact]
    public void IconOnlyButtonsHaveExplicitAccessibleNames()
    {
        foreach (var file in ApplicationXamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var unnamedButtons = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .Where(ContainsIconWithoutVisibleText)
                .Where(element => !HasAttribute(element, "AutomationProperties.Name"))
                .Select(DescribeElement)
                .ToArray();

            Assert.True(
                unnamedButtons.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains icon-only buttons without explicit accessible names: "
                + string.Join(", ", unnamedButtons));
        }
    }

    [Fact]
    public void InWindowOverlaysTrapKeyboardFocus()
    {
        foreach (var file in ApplicationXamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var unboundedOverlays = document
                .Descendants()
                .Where(element => element.Name.LocalName == "SurfaceCard"
                    && string.Equals(
                        AttributeValue(element, "Elevation"),
                        "Overlay",
                        StringComparison.Ordinal))
                .Where(element => !string.Equals(
                    AttributeValue(element, "KeyboardNavigation.TabNavigation"),
                    "Cycle",
                    StringComparison.Ordinal))
                .Select(DescribeElement)
                .ToArray();

            Assert.True(
                unboundedOverlays.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains overlays that do not cycle keyboard focus: "
                + string.Join(", ", unboundedOverlays));
        }
    }

    [Fact]
    public void AgentAuthorizationSurfacesReflowAtLargeTextScales()
    {
        var views = Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views");
        var mainWindow = ApplicationViews.FindUniqueOwnerDocument(
            "the agent authorization panel",
            element => element.Name.LocalName == "Border"
                && HasClass(element, "AgentPanel"))
            .Document;
        var agentPanel = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "AgentPanel"));
        var agentLayout = Assert.Single(
            agentPanel.Elements(),
            element => element.Name.LocalName == "Grid");

        Assert.Equal(
            "Auto,Auto,*,Auto,Auto",
            AttributeValue(agentLayout, "RowDefinitions"));
        Assert.All(
            agentLayout.Elements()
                .Where(element => element.Name.LocalName == "Grid")
                .Take(2),
            row => Assert.False(
                string.IsNullOrWhiteSpace(AttributeValue(row, "MinHeight")),
                "Text-bearing agent header rows require minima, not fixed heights."));
        Assert.Contains(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentRunScopeOptions}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "AI agent target scope",
                    StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(
                    AttributeValue(
                        element,
                        "AutomationProperties.HelpText")));

        var activityScroller = Assert.Single(
            agentLayout.Elements(),
            element => element.Name.LocalName == "ScrollViewer"
                && string.Equals(
                    AttributeValue(element, "Name"),
                    "AgentChatTranscript",
                    StringComparison.Ordinal));
        Assert.Equal("2", AttributeValue(activityScroller, "Grid.Row"));

        // The authority notice and the capability boundary are anchored to the
        // panel, not to the transcript. Inside the scroller they were the first
        // items in a list that grows, so a live YOLO grant scrolled out of sight
        // as soon as the run said anything — the moment it most needs to be read.
        Assert.Contains(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding AgentChat.HasYoloAuthority}",
                    StringComparison.Ordinal));
        Assert.Contains(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding AgentChat.HasCapabilityNotice}",
                    StringComparison.Ordinal));
        var contextInspector = Assert.Single(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "Expander"
                && string.Equals(
                    AttributeValue(element, "Name"),
                    "AgentContextInspector",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.HasContextItems}",
            AttributeValue(contextInspector, "IsVisible"));
        Assert.Contains(
            "AgentChat.ContextInspectorAccessibleName",
            AttributeValue(contextInspector, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(
                    contextInspector,
                    "AutomationProperties.HelpText")));
        Assert.Contains(
            contextInspector.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentChat.ContextItems}",
                    StringComparison.Ordinal));
        Assert.Contains(
            contextInspector.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "{Binding AccessibleName}",
                    StringComparison.Ordinal));
        var actionCancel = Assert.Single(
            activityScroller.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentActionClick",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.CanCancelActiveAction}",
            AttributeValue(actionCancel, "IsEnabled"));
        Assert.Contains(
            "ActiveActionCancellationLabel",
            AttributeValue(actionCancel, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(
                    actionCancel,
                    "AutomationProperties.HelpText")));
        Assert.Contains(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentChatClick",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding AgentChat.CanRequestStop}",
                    StringComparison.Ordinal));
        Assert.Contains(
            "AgentChat.Status",
            AttributeValue(activityScroller, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.Contains(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "StatusChip"
                && (AttributeValue(element, "AutomationProperties.Name") ?? string.Empty)
                    .Contains("AgentChat.ConnectionStatus", StringComparison.Ordinal));
        Assert.Contains(
            agentLayout.Descendants(),
            element => element.Name.LocalName == "Border"
                && (AttributeValue(element, "AutomationProperties.Name") ?? string.Empty)
                    .Contains("AgentChat.YoloAuthority.Scope", StringComparison.Ordinal));

        var dialog = XDocument.Load(
            Path.Combine(views, "AgentYoloConfirmationDialog.axaml"),
            LoadOptions.SetLineInfo);
        var window = Assert.IsType<XElement>(dialog.Root);
        Assert.Equal("True", AttributeValue(window, "CanResize"));
        Assert.Equal("OnOpened", AttributeValue(window, "Opened"));
        Assert.False(
            string.IsNullOrWhiteSpace(AttributeValue(window, "MaxHeight")));
        Assert.Contains(
            dialog.Descendants(),
            element => element.Name.LocalName == "ScrollViewer"
                && string.Equals(
                    AttributeValue(element, "VerticalScrollBarVisibility"),
                    "Auto",
                    StringComparison.Ordinal));
        Assert.Contains(
            dialog.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && element.Descendants().Any(child =>
                    child.Name.LocalName == "TextBlock"
                    && string.Equals(
                        AttributeValue(child, "TextWrapping"),
                        "Wrap",
                        StringComparison.Ordinal)));

        var acknowledgement = Assert.Single(
            dialog.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && string.Equals(
                    AttributeValue(element, "Name"),
                    "Acknowledgement",
                    StringComparison.Ordinal));
        Assert.Contains(
            "destructive terminal actions will not ask again",
            AttributeValue(acknowledgement, "AutomationProperties.Name"),
            StringComparison.OrdinalIgnoreCase);

        var confirm = Assert.Single(
            dialog.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Name"),
                    "ConfirmButton",
                    StringComparison.Ordinal));
        Assert.Equal("False", AttributeValue(confirm, "IsEnabled"));
        Assert.Equal("True", AttributeValue(confirm, "IsDefault"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(confirm, "AutomationProperties.Name")));

        var cancel = Assert.Single(
            dialog.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "IsCancel"),
                    "True",
                    StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(cancel, "AutomationProperties.Name")));

        var dialogCode = File.ReadAllText(
            Path.Combine(views, "AgentYoloConfirmationDialog.axaml.cs"));
        Assert.Contains(
            "FindControl<CheckBox>(\"Acknowledgement\")!.Focus()",
            dialogCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSelectedTerminalScopeUsesAnAccessibleExactChoiceList()
    {
        var mainWindow = ApplicationViews.FindUniqueOwnerDocument(
            "the selected-terminal agent scope chooser",
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding IsAgentSelectedPanelsScope}",
                    StringComparison.Ordinal))
            .Document;
        var chooser = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding IsAgentSelectedPanelsScope}",
                    StringComparison.Ordinal));

        Assert.Equal(
            "{Binding AgentChat.CanChangeProvider}",
            AttributeValue(chooser, "IsEnabled"));
        Assert.Equal(
            "Selected terminals for the AI agent",
            AttributeValue(chooser, "AutomationProperties.Name"));
        Assert.Contains(
            "stable IDs",
            AttributeValue(chooser, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            chooser.Descendants(),
            element => element.Name.LocalName == "TextBox");

        var choices = Assert.Single(
            chooser.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentTerminalSelectionOptions}",
                    StringComparison.Ordinal));
        var choice = Assert.Single(
            choices.Descendants(),
            element => element.Name.LocalName == "CheckBox");
        Assert.Equal(
            "{Binding IsSelected, Mode=TwoWay}",
            AttributeValue(choice, "IsChecked"));
        Assert.Equal(
            "{Binding AutomationName}",
            AttributeValue(choice, "AutomationProperties.Name"));
        Assert.Contains(
            "AutomationHelpText",
            AttributeValue(choice, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        Assert.Contains(
            choice.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding IdentityLabel}",
                    StringComparison.Ordinal));

        Assert.Contains(
            chooser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
        Assert.Contains(
            chooser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Assertive",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void SavedScreenDeleteUndoIsVisibleAndKeyboardReachable()
    {
        var notice = ApplicationViews
            .FindUniqueNamedElement("SavedScreenDeleteUndoNotice")
            .Element;

        Assert.Equal("SurfaceCard", notice.Name.LocalName);
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.HasPending}",
            AttributeValue(notice, "IsVisible"));
        Assert.Equal("Polite", AttributeValue(notice, "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.Status}",
            AttributeValue(notice, "AutomationProperties.Name"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(notice, "AutomationProperties.HelpText")));

        var undo = Assert.Single(
            notice.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Name"),
                    "UndoDeletedSavedScreenButton",
                    StringComparison.Ordinal));
        Assert.Equal(
            "OnUndoDeletedSavedScreenClick",
            AttributeValue(undo, "Click"));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.CanUndo}",
            AttributeValue(undo, "IsEnabled"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(undo, "AutomationProperties.Name")));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(undo, "AutomationProperties.HelpText")));

        var dismiss = Assert.Single(
            notice.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnDismissSavedScreenDeleteUndoClick",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.CanUndo}",
            AttributeValue(dismiss, "IsEnabled"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(dismiss, "AutomationProperties.Name")));
    }

    [Fact]
    public void RuntimeTabReorderingHasPointerFeedbackAndKeyboardParity()
    {
        // The strip is one reusable control hosted at whichever edge the profile
        // selects, so the tab template lives in that component rather than being
        // copied per edge.
        var ownedTabStrip = ApplicationViews.FindUniqueNamedElement(
            "TabScrollViewer");
        var tabStrip = ownedTabStrip.Element;

        // The reorder live region belongs to the shell route that hosts the strip.
        var workspace = ApplicationViews
            .FindUniqueNamedElement("RuntimeTabStripSide")
            .Owner
            .Document;

        Assert.Equal("ScrollViewer", tabStrip.Name.LocalName);
        // Scroll bars follow the strip's orientation instead of being fixed.
        Assert.Equal(
            "{Binding HorizontalScrollBars, ElementName=Root}",
            AttributeValue(tabStrip, "HorizontalScrollBarVisibility"));
        Assert.Equal(
            "{Binding VerticalScrollBars, ElementName=Root}",
            AttributeValue(tabStrip, "VerticalScrollBarVisibility"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(tabStrip, "AutomationProperties.Name")));

        var dropTarget = Assert.Single(
            tabStrip.Descendants(),
            element => element.Name.LocalName == "Grid"
                && HasClass(element, "RuntimeTabDropTarget"));
        Assert.Equal("True", AttributeValue(dropTarget, "DragDrop.AllowDrop"));
        Assert.Equal(
            "OnDragEnter",
            AttributeValue(dropTarget, "DragDrop.DragEnter"));
        Assert.Equal(
            "OnDragLeave",
            AttributeValue(dropTarget, "DragDrop.DragLeave"));
        Assert.Equal(
            "OnDragOver",
            AttributeValue(dropTarget, "DragDrop.DragOver"));
        Assert.Equal(
            "OnDrop",
            AttributeValue(dropTarget, "DragDrop.Drop"));

        var indicators = dropTarget
            .Elements()
            .Where(element => element.Name.LocalName == "Border"
                && HasClass(element, "RuntimeTabDropIndicator"))
            .ToArray();
        Assert.Equal(2, indicators.Length);
        Assert.Contains(indicators, indicator => HasClass(indicator, "Before"));
        Assert.Contains(indicators, indicator => HasClass(indicator, "After"));
        Assert.All(indicators, indicator =>
        {
            Assert.Equal("False", AttributeValue(indicator, "IsVisible"));
            Assert.Equal("False", AttributeValue(indicator, "IsHitTestVisible"));
            Assert.Contains(
                "ShellAccentBrush",
                AttributeValue(indicator, "Background"),
                StringComparison.Ordinal);
        });

        var dragHandle = Assert.Single(
            dropTarget.Elements(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "PointerPressed"),
                    "OnDragPointerPressed",
                    StringComparison.Ordinal));
        Assert.Equal(
            "OnDragPointerMoved",
            AttributeValue(dragHandle, "PointerMoved"));
        Assert.Equal(
            "OnDragPointerReleased",
            AttributeValue(dragHandle, "PointerReleased"));
        Assert.Equal(
            "OnDragPointerCaptureLost",
            AttributeValue(dragHandle, "PointerCaptureLost"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(dragHandle, "AutomationProperties.Name")));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(dragHandle, "AutomationProperties.HelpText")));
        Assert.Contains(
            dragHandle.Descendants(),
            element => element.Name.LocalName == "SymbolIcon"
                && string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ReOrderDotsVertical",
                    StringComparison.Ordinal));

        var mainWindowCodeBehind = ApplicationViews.FindPartialClassSources("MainWindow");
        var pointerPressedStart = mainWindowCodeBehind.IndexOf(
            "private void OnRuntimeTabDragPointerPressed(",
            StringComparison.Ordinal);
        var pointerMovedStart = mainWindowCodeBehind.IndexOf(
            "private void OnRuntimeTabDragPointerMoved(",
            StringComparison.Ordinal);
        Assert.True(pointerPressedStart >= 0);
        Assert.True(pointerMovedStart > pointerPressedStart);
        Assert.Contains(
            "e.Handled = true;",
            mainWindowCodeBehind[pointerPressedStart..pointerMovedStart],
            StringComparison.Ordinal);

        var activator = Assert.Single(
            dropTarget.Elements(),
            element => element.Name.LocalName == "Button"
                && HasClass(element, "RuntimeTabActivator"));
        Assert.Contains(
            "Activate tab",
            AttributeValue(activator, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Move tab left",
            AttributeValue(activator, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        var close = Assert.Single(
            dropTarget.Elements(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnClose",
                    StringComparison.Ordinal));
        Assert.Contains(
            "Title",
            AttributeValue(close, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(close, "AutomationProperties.HelpText")));

        var status = Assert.Single(
            workspace.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding TabReorderStatus}",
                    StringComparison.Ordinal));
        Assert.Equal("Polite", AttributeValue(status, "AutomationProperties.LiveSetting"));
        Assert.Contains(
            "TabReorderStatus",
            AttributeValue(status, "AutomationProperties.Name"),
            StringComparison.Ordinal);

        var codeBehind = ApplicationViews.FindPartialClassSources("MainWindow");
        Assert.Contains(
            "DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "new RuntimeTabActiveDrag(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowDragGhost(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveRuntimeTabDrop(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RuntimeTabDragThreshold",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.MoveTabAsync(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DataObject", codeBehind, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ApplicationXamlFiles()
    {
        var applicationRoot = Path.Combine(RepositoryRoot, "src", "GhostShell.App");
        return Directory
            .EnumerateFiles(applicationRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));
    }

    private static bool ContainsIconWithoutVisibleText(XElement button)
    {
        var containsIcon = button
            .Descendants()
            .Any(element => element.Name.LocalName == "SymbolIcon");
        var hasContent = button.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Content"
            && !string.IsNullOrWhiteSpace(attribute.Value));
        var containsText = button
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Any(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Text"
                && !string.IsNullOrWhiteSpace(attribute.Value)));
        return containsIcon && !hasContent && !containsText;
    }

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.Ordinal);

    private static bool HasAttribute(XElement element, string name) =>
        AttributeValue(element, name) is not null;

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;

    private static string DescribeElement(XElement element)
    {
        var line = (element as IXmlLineInfo)?.LineNumber ?? 0;
        var name = AttributeValue(element, "Name") ?? element.Name.LocalName;
        return line > 0 ? $"{name} at line {line}" : name;
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the GhostSHELL repository root.");
    }

    [GeneratedRegex(@"\bpane(s)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaneTerminology();

    [GeneratedRegex(
        "<(?<element>[A-Za-z0-9:]+)\\b[^>]*\\bFontSize=\"[0-9]",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LiteralElementFontSize();

    [GeneratedRegex(
        "<Setter\\s+Property=\"FontSize\"\\s+Value=\"[0-9]",
        RegexOptions.CultureInvariant)]
    private static partial Regex LiteralFontSizeSetter();
}
