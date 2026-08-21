using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed partial class RepositoryConventionTests
{
    [Fact]
    public void Connection_editor_keeps_test_progress_in_its_fixed_footer()
    {
        var dialog = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "ConnectionEditorDialog.axaml"));
        var root = Assert.IsType<XElement>(dialog.Root);
        var shell = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "DialogShell", StringComparison.Ordinal));
        var test = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Click"), "OnTestClick", StringComparison.Ordinal));

        Assert.Equal("{Binding TestFooterHint}", AttributeValue(shell, "FooterHint"));
        Assert.Equal("{Binding !IsTesting}", AttributeValue(test, "IsEnabled"));
        Assert.Equal("{Binding TestLabel}", AttributeValue(test, "Content"));
    }

    [Fact]
    public void Database_password_prompt_offers_explicit_opt_in_credential_storage()
    {
        var dialog = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "DatabasePasswordPromptDialog.axaml"));
        var root = Assert.IsType<XElement>(dialog.Root);
        var savePassword = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name"), "SavePasswordCheckBox", StringComparison.Ordinal));

        Assert.Equal("CheckBox", savePassword.Name.LocalName);
        Assert.Equal("False", AttributeValue(savePassword, "IsChecked"));
        Assert.Equal("False", AttributeValue(savePassword, "IsVisible"));
        Assert.Equal(
            "Save password in system credential store",
            AttributeValue(savePassword, "AutomationProperties.Name"));
    }

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
            "signing_identifier=\"app.ghostshell\"",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            declarationScript.Split(
                "--identifier \"${signing_identifier}\"",
                StringSplitOptions.None).Length - 1);
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
    public void Macos_release_packages_a_speed_optimized_native_aot_executable()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var workflow = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                ".github",
                "workflows",
                "repository-gate.yml"));
        var infoPlist = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "tools",
                "GhostShell.Packaging",
                "MacOS",
                "Info.plist.template"));

        Assert.Contains("<PublishAot>true</PublishAot>", desktopProject, StringComparison.Ordinal);
        Assert.Contains(
            "<OptimizationPreference>Speed</OptimizationPreference>",
            desktopProject,
            StringComparison.Ordinal);
        Assert.Contains("<StripSymbols>true</StripSymbols>", desktopProject, StringComparison.Ordinal);
        Assert.Contains("-p:GhostShellMacReleaseNativeAot=true", packageScript, StringComparison.Ordinal);
        Assert.Contains("packages.${runtime_identifier}.aot.lock.json", packageScript, StringComparison.Ordinal);
        Assert.Contains("--managed-evidence", packageScript, StringComparison.Ordinal);
        Assert.Contains("*.runtimeconfig.json", packageScript, StringComparison.Ordinal);
        Assert.Contains("brew install lld@22", workflow, StringComparison.Ordinal);
        Assert.Contains("<string>GhostShell.icns</string>", infoPlist, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "assets",
            "macos",
            "GhostShell.icns")));
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "assets",
            "macos",
            "GhostShell.icon",
            "icon.json")));
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
        Assert.Equal(
            3,
            packageScript.Split(
                "-p:GhostShellProductVersion=\"${version}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "nuget_packages=\"${NUGET_PACKAGES:-${repository_dir}/.nuget/packages}\"",
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
        Assert.Contains("\"GhostShell.Docker.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Docking.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Git.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Previews.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Redis.dll\"", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_dotnet_run_assembles_the_required_cef_application_bundle()
    {
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var developmentRunner = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "run-macos-development.sh"));

        Assert.Contains("ConfigureMacOsDevelopmentRun", desktopProject, StringComparison.Ordinal);
        Assert.Contains(
            "BeforeTargets=\"ComputeRunArguments\"",
            desktopProject,
            StringComparison.Ordinal);
        Assert.Contains("scripts/run-macos-development.sh", desktopProject, StringComparison.Ordinal);
        Assert.Contains("Chromium Embedded Framework.framework", developmentRunner, StringComparison.Ordinal);
        Assert.Contains("GhostSHELL Helper (Renderer)", developmentRunner, StringComparison.Ordinal);
        Assert.Contains("Contents/MacOS/GhostShell", developmentRunner, StringComparison.Ordinal);
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
                .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
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
                .Where(element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && string.Equals(
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
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"))
            .Document;
        var agentPanel = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"));
        var agentLayout = Assert.Single(
            agentPanel.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal));

        Assert.Equal(
            "Auto,Auto,*,Auto,Auto",
            AttributeValue(agentLayout, "RowDefinitions"));
        Assert.All(
            agentLayout.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal))
                .Take(2),
            row => Assert.False(
                string.IsNullOrWhiteSpace(AttributeValue(row, "MinHeight")),
                "Text-bearing agent header rows require minima, not fixed heights."));
        var activityScroller = Assert.Single(
            agentLayout.Elements(),
            element => string.Equals(element.Name.LocalName, "ScrollViewer"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Name"),
                    "AgentChatTranscript",
                    StringComparison.Ordinal));
        Assert.Equal("2", AttributeValue(activityScroller, "Grid.Row"));

        var contextInspector = Assert.Single(
            agentLayout.Descendants(),
            element => string.Equals(element.Name.LocalName, "Expander"
, StringComparison.Ordinal) && string.Equals(
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
            element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentChat.ContextItems}",
                    StringComparison.Ordinal));
        Assert.Contains(
            contextInspector.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "{Binding AccessibleName}",
                    StringComparison.Ordinal));
        var actionCancel = Assert.Single(
            activityScroller.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
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
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentChatClick",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding AgentChat.CanRequestStop}",
                    StringComparison.Ordinal));
        Assert.Equal(
            "AI agent activity",
            AttributeValue(activityScroller, "AutomationProperties.Name"));
        Assert.Contains(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "StatusChip"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "AI agent state", StringComparison.Ordinal));
        Assert.Contains(
            agentLayout.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "{Binding AgentChat.Status}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "AI agent status", StringComparison.Ordinal));
        Assert.DoesNotContain(
            agentLayout.Descendants(),
            element => (AttributeValue(element, "Text") ?? string.Empty)
                .Contains("Expires ·", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentPanelDoesNotExposeLegacyTargetSelectors()
    {
        var mainWindow = ApplicationViews.FindUniqueOwnerDocument(
            "the AI agent panel",
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"))
            .Document;
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentRunScopeOptions}",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentTerminalSelectionOptions}",
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
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
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
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
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
    public void RuntimeTabIconIsOpticallyAlignedWithTitle()
    {
        var tabStrip = ApplicationViews.FindUniqueNamedElement("TabScrollViewer").Element;
        var title = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabTitle"));
        var icon = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol")
, "{Binding IconSymbol}", StringComparison.Ordinal));
        var opticalOffset = Assert.Single(
            icon.Descendants(),
            element => string.Equals(element.Name.LocalName, "TranslateTransform"
, StringComparison.Ordinal));

        Assert.Equal("Center", AttributeValue(title, "VerticalAlignment"));
        Assert.Equal("Center", AttributeValue(icon, "VerticalAlignment"));
        Assert.Equal("-1", AttributeValue(opticalOffset, "Y"));
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
        // Scroll bars follow the strip's orientation. Assigned from code, not
        // bound: the consumer sets Orientation after the strip's own bindings
        // read their value, and the stale Hidden axis let a side-docked strip
        // grow past its viewport and clip its close buttons.
        var stripCode = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            "RuntimeTabStripView.axaml.cs"));
        Assert.Contains("private void SyncScrollBars()", stripCode, StringComparison.Ordinal);
        Assert.Contains(
            "TabScrollViewer.HorizontalScrollBarVisibility",
            stripCode,
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(tabStrip, "AutomationProperties.Name")));

        var dropTarget = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabDropTarget"));
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
            .Where(element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabDropIndicator"))
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

        var activator = Assert.Single(
            dropTarget.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabActivator"));
        Assert.Null(AttributeValue(activator, "PointerMoved"));
        Assert.Null(AttributeValue(activator, "PointerReleased"));
        Assert.Null(AttributeValue(activator, "PointerCaptureLost"));
        Assert.Null(AttributeValue(activator, "PointerPressed"));
        Assert.Contains("OnTabPointerPressed", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerMoved", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerReleased", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerCaptureLost", stripCode, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", stripCode, StringComparison.Ordinal);
        Assert.Contains("FindTabActivator(e.Source)", stripCode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            dropTarget.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(
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
        Assert.DoesNotContain(
            "e.Handled = true;",
            mainWindowCodeBehind[pointerPressedStart..pointerMovedStart],
            StringComparison.Ordinal);

        Assert.Contains(
            "Activate tab",
            AttributeValue(activator, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Drag the tab to reorder it",
            AttributeValue(activator, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        var close = Assert.Single(
            dropTarget.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
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
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
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
            .Any(element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal));
        var hasContent = button.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Content"
, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value));
        var containsText = button
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TextBlock", StringComparison.Ordinal))
            .Any(element => element.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Text"
, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value)));
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
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
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

    [GeneratedRegex(
        @"\bpanes?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex PaneTerminology();

    [GeneratedRegex(
        "<(?<element>[A-Za-z0-9:]+)\\b[^>]*\\bFontSize=\"[0-9]",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex LiteralElementFontSize();

    [GeneratedRegex(
        "<Setter\\s+Property=\"FontSize\"\\s+Value=\"[0-9]",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex LiteralFontSizeSetter();
}
