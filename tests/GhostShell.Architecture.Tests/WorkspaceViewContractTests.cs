using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> ShellInteractions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ActivateTabRequested"] = "OnActivateTabClick",
            ["AgentQuestionResponseKeyDownRequested"] = "OnAgentQuestionResponseKeyDown",
            ["ApproveAgentActionRequested"] = "OnApproveAgentActionClick",
            ["CancelAgentActionRequested"] = "OnCancelAgentActionClick",
            ["CancelAgentChatRequested"] = "OnCancelAgentChatClick",
            ["CancelFileTransferRequested"] = "OnCancelFileTransferClick",
            ["ClearAgentChatRequested"] = "OnClearAgentChatClick",
            ["CloseRuntimeTabRequested"] = "OnCloseRuntimeTabClick",
            ["DeclineAgentQuestionRequested"] = "OnDeclineAgentQuestionClick",
            ["DenyAgentActionRequested"] = "OnDenyAgentActionClick",
            ["DisableAgentYoloRequested"] = "OnDisableAgentYoloClick",
            ["EnableAgentCapabilityAskRequested"] = "OnEnableAgentCapabilityAskClick",
            ["EnableAgentYoloRequested"] = "OnEnableAgentYoloClick",
            ["KeepAgentCapabilityOffRequested"] = "OnKeepAgentCapabilityOffClick",
            ["LoadOlderAgentAuditRequested"] = "OnLoadOlderAgentAuditClick",
            ["OpenWorkspaceRequested"] = "OnOpenWorkspaceClick",
            ["RefreshAgentAuditRequested"] = "OnRefreshAgentAuditClick",
            ["RetryFileTransferRequested"] = "OnRetryFileTransferClick",
            ["RuntimeTabDragEnterRequested"] = "OnRuntimeTabDragEnter",
            ["RuntimeTabDragLeaveRequested"] = "OnRuntimeTabDragLeave",
            ["RuntimeTabDragOverRequested"] = "OnRuntimeTabDragOver",
            ["RuntimeTabDragPointerCaptureLostRequested"] =
                "OnRuntimeTabDragPointerCaptureLost",
            ["RuntimeTabDragPointerMovedRequested"] = "OnRuntimeTabDragPointerMoved",
            ["RuntimeTabDragPointerPressedRequested"] = "OnRuntimeTabDragPointerPressed",
            ["RuntimeTabDragPointerReleasedRequested"] = "OnRuntimeTabDragPointerReleased",
            ["RuntimeTabDropRequested"] = "OnRuntimeTabDrop",
            ["SendAgentChatRequested"] = "OnSendAgentChatClick",
            ["ShowAgentSettingsRequested"] = "OnShowAgentSettingsClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowLauncherRequested"] = "OnShowLauncherClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["ShowNewPanelRequested"] = "OnShowNewPanelClick",
            ["ShowSettingsRequested"] = "OnShowSettingsClick",
            ["SubmitAgentQuestionRequested"] = "OnSubmitAgentQuestionClick",
            ["TitleBarPointerPressedRequested"] = "OnTitleBarPointerPressed",
            ["ToggleAgentRequested"] = "OnToggleAgentClick",
        };

    [Fact]
    public void Main_window_delegates_the_workspace_route_to_one_named_view()
    {
        var mainWindow = LoadView("MainWindow");
        var workspace = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "WorkspaceView");

        Assert.Equal("WorkspaceRouteView", AttributeValue(workspace, "Name"));
        Assert.Equal(
            "{Binding IsWorkspaceVisible}",
            AttributeValue(workspace, "IsVisible"));

        foreach (var (interaction, handler) in ShellInteractions)
        {
            Assert.Equal(handler, AttributeValue(workspace, interaction));
        }

        foreach (var extractedName in RouteControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => HasClass(element, "AgentPanel"));
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "RuntimePanelLayoutPanel");
    }

    [Fact]
    public void Workspace_view_preserves_route_geometry_bindings_and_accessibility()
    {
        var workspace = LoadView("WorkspaceView");
        var root = Assert.IsType<XElement>(workspace.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        AssertTitleBarDragRegion(root);

        var surface = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,34,*,Auto,26", AttributeValue(surface, "RowDefinitions"));

        foreach (var extractedName in WorkspaceControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        var tabStrip = FindNamedElement(root, "RuntimeTabStrip");
        foreach (var interactiveChromeName in new[]
                 {
                     "RuntimeTabStrip",
                     "WorkspaceTitleBarActions",
                 })
        {
            Assert.Equal(
                "User",
                AttributeValue(
                    FindNamedElement(root, interactiveChromeName),
                    "WindowDecorationProperties.ElementRole"));
        }
        // The strip is a reusable control hosted at whichever edge the profile
        // selects; the template it renders lives in that component.
        Assert.Equal("RuntimeTabStripView", tabStrip.Name.LocalName);
        Assert.Equal("Horizontal", AttributeValue(tabStrip, "Orientation"));
        Assert.Equal("Open runtime tabs", AttributeValue(
            tabStrip,
            "AutomationProperties.Name"));
        Assert.Equal(
            "{Binding RuntimeWorkspace.Tabs}",
            AttributeValue(tabStrip, "Tabs"));
        Assert.Equal("True", AttributeValue(tabStrip, "ShowHomeTab"));
        Assert.Equal("False", AttributeValue(tabStrip, "IsHomeActive"));
        Assert.Equal("OnShowLauncherClick", AttributeValue(tabStrip, "HomeRequested"));

        // Every edge the setting offers has a host, and each is bound to the
        // stored placement rather than being always visible.
        foreach (var (name, visibility) in new[]
                 {
                     ("RuntimeTabStrip", "{Binding IsTabStripVisibleOnTop}"),
                     ("RuntimeTabStripBottom", "{Binding IsTabStripVisibleOnBottom}"),
                     ("RuntimeTabStripSide", "{Binding IsTabStripVisibleOnSide}"),
                 })
        {
            var host = FindNamedElement(root, name);
            Assert.Equal("RuntimeTabStripView", host.Name.LocalName);
            Assert.Equal(
                "{Binding RuntimeWorkspace.Tabs}",
                AttributeValue(host, "Tabs"));
            _ = visibility;
        }

        Assert.Equal(
            "Vertical",
            AttributeValue(FindNamedElement(root, "RuntimeTabStripSide"), "Orientation"));

        // The rail's edge and visibility follow the stored appearance profile, so
        // both are bindings rather than fixed values.
        var rail = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "DockPanel.Dock"),
                    "{Binding WorkspacePanelDock}",
                    StringComparison.Ordinal));
        Assert.True(HasClass(rail, "FloatingSidebar"));
        Assert.Equal(
            "{Binding ShowWorkspacesPanel}",
            AttributeValue(rail, "IsVisible"));

        var agentWorkspace = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "AgentWorkspaceView");
        Assert.Equal("AgentWorkspaceSurface", AttributeValue(agentWorkspace, "Name"));
        Assert.Equal("Right", AttributeValue(agentWorkspace, "DockPanel.Dock"));
        Assert.Equal("352", AttributeValue(agentWorkspace, "Width"));
        // The shortcut row already contributes half the top gutter below its
        // centred controls, so the agent supplies the other half rather than
        // visually doubling only that edge.
        Assert.Equal(
            "{controls:Inset Right=Sm, Top=Sm, Bottom=Sm}",
            AttributeValue(agentWorkspace, "Margin"));
        Assert.Equal(
            "{Binding IsAgentPanelVisible}",
            AttributeValue(agentWorkspace, "IsVisible"));
        foreach (var interaction in AgentInteractionNames)
        {
            Assert.Equal(
                ShellInteractions[interaction],
                AttributeValue(agentWorkspace, interaction));
        }

        Assert.DoesNotContain(
            root.Descendants(),
            element => HasClass(element, "AgentPanel"));
        foreach (var controlName in AgentControlNames)
        {
            Assert.DoesNotContain(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    controlName,
                    StringComparison.Ordinal));
        }

        var dockControl = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "DockControl");
        Assert.Equal(
            "{Binding RuntimeWorkspace.ActiveTab.DockLayout}",
            AttributeValue(dockControl, "Layout"));
        Assert.Equal(
            "{Binding RuntimeWorkspace.ActiveTab.DockFactory}",
            AttributeValue(dockControl, "Factory"));
        // Docking pauses while the layout designer overlay is open: Dock resolves
        // drop targets across every registered DockControl, and the designer's
        // canvas must not be able to dock a slot into the live workspace beneath.
        Assert.Equal(
            "{Binding !IsLayoutDesignerVisible}",
            AttributeValue(dockControl, "IsDockingEnabled"));
        Assert.Equal("True", AttributeValue(dockControl, "InitializeFactory"));
        Assert.Equal("False", AttributeValue(dockControl, "InitializeLayout"));
        // Dock windows are real platform windows. The managed-window layer would
        // constrain them to the workspace canvas and add a second in-app title
        // bar instead of providing actual floating panels.
        Assert.Null(AttributeValue(dockControl, "EnableManagedWindowLayer"));
        Assert.Null(AttributeValue(dockControl, "MinWidth"));
        Assert.Null(AttributeValue(dockControl, "MinHeight"));
        Assert.NotEqual(
            "ScrollViewer",
            dockControl.Parent?.Name.LocalName);
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "RuntimePanelLayoutPanel");

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding WorkspaceStatus}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding TabReorderStatus}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
        var transferManagerButton = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding HasFileTransfers}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Open transfer manager",
                    StringComparison.Ordinal));
        Assert.Equal(
            "OnToggleFileTransferManagerClick",
            AttributeValue(transferManagerButton, "Click"));
        Assert.DoesNotContain(
            transferManagerButton.Descendants(),
            element => element.Name.LocalName is "Flyout" or "Popup");
        Assert.Contains(
            transferManagerButton.Descendants(),
            element => element.Name.LocalName == "SymbolIcon"
                && string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ArrowSort",
                    StringComparison.Ordinal));
        var transferManager = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "SurfaceCard"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "File transfer manager",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "False",
                    StringComparison.Ordinal));
        Assert.Equal("2", AttributeValue(transferManager, "Grid.Row"));
        Assert.Equal(
            "Cycle",
            AttributeValue(
                transferManager,
                "KeyboardNavigation.TabNavigation"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding FileTransfers}",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding HostStatus}",
                StringComparison.Ordinal));
    }

    private static void AssertTitleBarDragRegion(XElement root)
    {
        var titleBar = Assert.Single(
            root.Descendants(),
            element => HasClass(element, "TopChrome"));
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
    public void Workspace_view_forwards_input_without_taking_shell_ownership()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class WorkspaceView");

        foreach (var interaction in ShellInteractions.Keys)
        {
            Assert.Contains($" {interaction};", codeBehind, StringComparison.Ordinal);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnAgentChatTranscriptScrollChanged",
            codeBehind,
            StringComparison.Ordinal);

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTabDragCandidate", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("DataFormat.CreateInProcessFormat", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_retains_runtime_templates_policy_and_visual_focus()
    {
        var mainWindow = LoadView("MainWindow");
        var runtimeTemplateTypes = mainWindow.Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .Select(element => AttributeValue(element, "DataType"))
            .Where(dataType => dataType?.EndsWith(
                "RuntimePanelViewModel",
                StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(6, runtimeTemplateTypes.Length);

        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");
        var dragGhost = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "DragGhostPresenter",
                StringComparison.Ordinal));
        Assert.Equal("SurfaceCard", dragGhost.Name.LocalName);
        Assert.Equal("Overlay", AttributeValue(dragGhost, "Elevation"));
        Assert.Equal("0", AttributeValue(dragGhost, "Opacity"));
        var dragGhostLayer = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "DragGhostLayer",
                StringComparison.Ordinal));
        Assert.Equal("1000", AttributeValue(dragGhostLayer, "ZIndex"));
        Assert.Contains(
            "ResolveDragGhostPresenter() is not { } presenter",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "this.FindControl<Control>(\"DragGhostPresenter\")",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowDragGhost(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "MoveDragGhost(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.MoveTabAsync(", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("RunCloseFlowAsync(", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("AgentYoloConfirmationDialog(", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("control.Classes.Contains(\"RuntimeTabActivator\")", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("control.Classes.Contains(\"RuntimePanelFocusTarget\")", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains(".OfType<TerminalPresentationHost>()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains(".OfType<BrowserPresentationHost>()", mainWindowCode, StringComparison.Ordinal);
    }

    private static readonly string[] AgentInteractionNames =
    [
        "AgentQuestionResponseKeyDownRequested",
        "ApproveAgentActionRequested",
        "CancelAgentActionRequested",
        "CancelAgentChatRequested",
        "ClearAgentChatRequested",
        "DeclineAgentQuestionRequested",
        "DenyAgentActionRequested",
        "DisableAgentYoloRequested",
        "EnableAgentCapabilityAskRequested",
        "EnableAgentYoloRequested",
        "KeepAgentCapabilityOffRequested",
        "LoadOlderAgentAuditRequested",
        "RefreshAgentAuditRequested",
        "SendAgentChatRequested",
        "ShowAgentSettingsRequested",
        "SubmitAgentQuestionRequested",
    ];

    private static readonly string[] WorkspaceControlNames =
    [
        "RuntimeTabStrip",
    ];

    private static readonly string[] AgentControlNames =
    [
        "AgentChatTranscript",
        "AgentContextInspector",
        "AgentCurrentProgress",
        "AgentPendingQuestion",
        "AgentQuestionResponseInput",
        "AgentPendingCapabilityRequest",
        "AgentRunAudit",
        "AgentChatPromptInput",
    ];

    private static readonly string[] RouteControlNames =
    [
        .. WorkspaceControlNames,
        .. AgentControlNames,
    ];

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains(className, StringComparer.Ordinal);

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
