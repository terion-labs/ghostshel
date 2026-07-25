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

        foreach (var extractedName in ExtractedControlNames)
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

        var surface = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("44,34,*,26", AttributeValue(surface, "RowDefinitions"));

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        var tabStrip = FindNamedElement(root, "RuntimeTabStrip");
        Assert.Equal("Auto", AttributeValue(tabStrip, "HorizontalScrollBarVisibility"));
        Assert.Equal("Disabled", AttributeValue(tabStrip, "VerticalScrollBarVisibility"));
        Assert.Equal("Open runtime tabs", AttributeValue(
            tabStrip,
            "AutomationProperties.Name"));
        Assert.Contains(
            tabStrip.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding RuntimeWorkspace.Tabs}",
                    StringComparison.Ordinal));

        var rail = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "DockPanel.Dock"),
                    "Left",
                    StringComparison.Ordinal));
        Assert.Equal("0,0,1,0", AttributeValue(rail, "BorderThickness"));

        var agentPanel = Assert.Single(
            root.Descendants(),
            element => HasClass(element, "AgentPanel"));
        Assert.Equal("Right", AttributeValue(agentPanel, "DockPanel.Dock"));
        Assert.Equal(
            "{Binding IsAgentPanelVisible}",
            AttributeValue(agentPanel, "IsVisible"));

        var panelItems = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding RuntimeWorkspace.ActiveTab.Panels}",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding RuntimeWorkspace.ActiveTab.MinimumCanvasWidth}",
            AttributeValue(panelItems, "MinWidth"));
        Assert.Equal(
            "{Binding RuntimeWorkspace.ActiveTab.MinimumCanvasHeight}",
            AttributeValue(panelItems, "MinHeight"));
        Assert.Contains(
            panelItems.Descendants(),
            element => element.Name.LocalName == "RuntimePanelLayoutPanel");

        var transcript = FindNamedElement(root, "AgentChatTranscript");
        Assert.Equal(
            "OnAgentChatTranscriptScrollChanged",
            AttributeValue(transcript, "ScrollChanged"));
        Assert.Contains(
            "AI agent activity",
            AttributeValue(transcript, "AutomationProperties.Name"),
            StringComparison.Ordinal);

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
            "private static void OnAgentChatTranscriptScrollChanged(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(transcript.ScrollToEnd);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AgentChatTranscriptScrollChangedRequested",
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

        var mainWindowCode = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class MainWindow");
        Assert.Contains(
            "DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>",
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

    private static readonly string[] ExtractedControlNames =
    [
        "RuntimeTabStrip",
        "AgentChatTranscript",
        "AgentContextInspector",
        "AgentCurrentProgress",
        "AgentPendingQuestion",
        "AgentQuestionResponseInput",
        "AgentPendingCapabilityRequest",
        "AgentRunAudit",
        "AgentChatPromptInput",
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
