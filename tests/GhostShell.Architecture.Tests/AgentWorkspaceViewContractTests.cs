using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class AgentWorkspaceViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> Interactions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AgentQuestionResponseKeyDownRequested"] =
                "OnAgentQuestionResponseKeyDown",
            ["ApproveAgentActionRequested"] = "OnApproveAgentActionClick",
            ["CancelAgentActionRequested"] = "OnCancelAgentActionClick",
            ["CancelAgentChatRequested"] = "OnCancelAgentChatClick",
            ["ClearAgentChatRequested"] = "OnClearAgentChatClick",
            ["DeclineAgentQuestionRequested"] = "OnDeclineAgentQuestionClick",
            ["DenyAgentActionRequested"] = "OnDenyAgentActionClick",
            ["DisableAgentYoloRequested"] = "OnDisableAgentYoloClick",
            ["EnableAgentCapabilityAskRequested"] =
                "OnEnableAgentCapabilityAskClick",
            ["EnableAgentYoloRequested"] = "OnEnableAgentYoloClick",
            ["KeepAgentCapabilityOffRequested"] =
                "OnKeepAgentCapabilityOffClick",
            ["LoadOlderAgentAuditRequested"] = "OnLoadOlderAgentAuditClick",
            ["StartNewAgentConversationRequested"] = "OnStartNewConversationClick",
            ["OpenAgentConversationRequested"] = "OnOpenConversationClick",
            ["DeleteAgentConversationRequested"] = "OnDeleteConversationClick",
            ["CopyAgentMessageRequested"] = "OnCopyAgentMessageClick",
            ["ForkAgentConversationRequested"] = "OnForkAgentConversationClick",
            ["SelectAgentModelRequested"] = "OnSelectModelClick",
            ["ToggleAgentModelFavoriteRequested"] =
                "OnToggleFavoriteModelClick",
            ["RefreshAgentModelsRequested"] = "OnRefreshModelsClick",
            ["RefreshAgentAuditRequested"] = "OnRefreshAgentAuditClick",
            ["SendAgentChatRequested"] = "OnSendAgentChatClick",
            ["ShowAgentSettingsRequested"] = "OnShowAgentSettingsClick",
            ["SubmitAgentQuestionRequested"] = "OnSubmitAgentQuestionClick",
        };

    [Fact]
    public void Agent_workspace_owns_the_complete_pixel_stable_agent_surface()
    {
        var document = LoadView();
        var root = Assert.IsType<XElement>(document.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        Assert.Null(AttributeValue(root, "DataContext"));

        // The root is a wrapper hosting the surface and, over its bottom-left
        // corner, the floating-mode resize grip.
        var host = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Panel");
        var panel = Assert.Single(
            host.Elements(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "AgentPanel"));
        var layout = Assert.Single(
            panel.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,Auto,*,Auto,Auto", AttributeValue(layout, "RowDefinitions"));

        // The ordinary floating surface uses the corner grip. Edge-attached
        // hosts reuse it as a full inner-edge handle, including while docked.
        var resizeGrip = Assert.Single(
            host.Elements(),
            element => element.Name.LocalName == "Thumb"
                && AttributeValue(element, "Name") == "FloatingResizeHandle");
        Assert.Equal("FloatingResizeHandle", AttributeValue(resizeGrip, "Name"));
        Assert.Equal("OnFloatingResizeDragDelta", AttributeValue(resizeGrip, "DragDelta"));
        Assert.Null(AttributeValue(resizeGrip, "HorizontalAlignment"));
        Assert.Null(AttributeValue(resizeGrip, "VerticalAlignment"));
        var defaultResizeStyle = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "views|AgentWorkspaceView Thumb#FloatingResizeHandle");
        Assert.Contains(
            defaultResizeStyle.Elements(),
            element => AttributeValue(element, "Property") == "HorizontalAlignment"
                && AttributeValue(element, "Value") == "Left");
        Assert.Contains(
            defaultResizeStyle.Elements(),
            element => AttributeValue(element, "Property") == "VerticalAlignment"
                && AttributeValue(element, "Value") == "Bottom");
        var edgeResizeStyle = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "views|AgentWorkspaceView.edgeResizable Thumb#FloatingResizeHandle");
        Assert.Contains(
            edgeResizeStyle.Elements(),
            element => AttributeValue(element, "Property") == "IsVisible"
                && AttributeValue(element, "Value") == "True");
        Assert.Contains(
            root.Descendants(),
            element => AttributeValue(element, "Name") == "CornerResizeGlyph");
        var heightResizeGrip = Assert.Single(
            host.Elements(),
            element => element.Name.LocalName == "Thumb"
                && AttributeValue(element, "Name") == "FloatingHeightResizeHandle");
        Assert.Equal(
            "OnFloatingHeightResizeDragDelta",
            AttributeValue(heightResizeGrip, "DragDelta"));

        foreach (var controlName in NamedControls)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    controlName,
                    StringComparison.Ordinal));
        }

        var transcript = FindNamedElement(root, "AgentChatTranscript");
        Assert.Equal(
            "OnAgentChatTranscriptScrollChanged",
            AttributeValue(transcript, "ScrollChanged"));
        Assert.Contains(
            "AI agent activity",
            AttributeValue(transcript, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "IsVisible")
                == "{Binding AgentChat.CanStartConversation}");

        var providerSettings = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    "Open AI settings",
                    StringComparison.Ordinal));
        Assert.Equal("PrimaryButton", AttributeValue(providerSettings, "Classes"));
        Assert.Equal(
            "Open AI settings",
            AttributeValue(providerSettings, "AutomationProperties.Name"));
        Assert.False(string.IsNullOrWhiteSpace(
            AttributeValue(providerSettings, "AutomationProperties.HelpText")));

        var footerStatus = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "Text") == "{Binding AgentChat.Status}");
        Assert.Equal("Center", AttributeValue(footerStatus, "HorizontalAlignment"));
        Assert.Equal("Center", AttributeValue(footerStatus, "TextAlignment"));

        var clearRetainedSession = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    "Clear agent session",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.CanClear}",
            AttributeValue(clearRetainedSession, "IsVisible"));
        Assert.False(string.IsNullOrWhiteSpace(
            AttributeValue(
                clearRetainedSession,
                "AutomationProperties.Name")));

        var prompt = FindNamedElement(root, "AgentChatPromptInput");
        var composer = Assert.Single(
            prompt.Ancestors(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "Grid.Row"),
                    "3",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.HasProvider}",
            AttributeValue(composer, "IsVisible"));

        var contextUsage = Assert.Single(
            composer.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "AutomationProperties.Name")
                    == "{Binding AgentChat.ContextWindowUsageLabel}");
        Assert.Null(AttributeValue(contextUsage, "Content"));
        Assert.Equal("34", AttributeValue(contextUsage, "Width"));
        Assert.Equal("34", AttributeValue(contextUsage, "Height"));
        var contextDonut = Assert.Single(
            contextUsage.Elements(),
            element => element.Name.LocalName == "ContextWindowDonut");
        Assert.Equal(
            "{Binding AgentChat.ContextWindowPercent}",
            AttributeValue(contextDonut, "Percentage"));
        var composerToolbar = FindNamedElement(root, "AgentComposerToolbar");
        Assert.Equal(
            "Auto,*,Auto,*,Auto",
            AttributeValue(composerToolbar, "ColumnDefinitions"));
        Assert.Equal("0", AttributeValue(composerToolbar, "ColumnSpacing"));
        var accessMode = Assert.Single(
            composerToolbar.Elements(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "AutomationProperties.Name")
                    == "Choose how AI actions are approved");
        Assert.Equal("0", AttributeValue(accessMode, "MinWidth"));
        Assert.Contains(
            accessMode.Elements(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "TextTrimming")
                    == "CharacterEllipsis");
        var modelPicker = FindNamedElement(root, "AgentModelPickerButton");
        Assert.Equal("0", AttributeValue(modelPicker, "MinWidth"));
        Assert.Contains(
            modelPicker.Elements(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "TextTrimming")
                    == "CharacterEllipsis");

        var stop = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentChatClick",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.ShowStopAction}",
            AttributeValue(stop, "IsVisible"));

        var committedReasoning = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "StackPanel"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "AI reasoning summary",
                    StringComparison.Ordinal));
        var reasoningDisclosure = Assert.Single(
            committedReasoning.Descendants(),
            element => element.Name.LocalName == "ToggleButton"
                && AttributeValue(element, "Name")
                    == "CommittedReasoningDisclosure");
        Assert.Equal("False", AttributeValue(reasoningDisclosure, "IsChecked"));
        Assert.Contains(
            committedReasoning.Descendants(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "BorderThickness") == "1,0,0,0"
                && AttributeValue(element, "IsVisible")
                    == "{Binding IsChecked, ElementName=CommittedReasoningDisclosure}");
        Assert.Contains(
            committedReasoning.Descendants(),
            element => element.Name.LocalName == "MarkdownPreviewView"
                && AttributeValue(element, "Text")
                    == "{Binding ReasoningSummaryDisplay}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "MarkdownPreviewView"
                && AttributeValue(element, "Text")
                    == "{Binding AgentChat.ProvisionalReasoningStageDisplay}");
        var reasoningLoader = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ProgressBar"
                && AttributeValue(element, "AutomationProperties.Name")
                    == "Reasoning in progress");
        Assert.Equal("1", AttributeValue(reasoningLoader, "Grid.Row"));
        Assert.Equal("2", AttributeValue(reasoningLoader, "Grid.ColumnSpan"));
        Assert.Equal("0", AttributeValue(reasoningLoader, "MinWidth"));
        Assert.Equal("Stretch", AttributeValue(reasoningLoader, "HorizontalAlignment"));
        Assert.Equal(
            "{Binding AgentChat.ShowProvisionalReasoningLoader}",
            AttributeValue(reasoningLoader, "IsVisible"));
        var provisionalReasoningBody = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "IsVisible")
                    == "{Binding IsChecked, ElementName=ProvisionalReasoningDisclosure}");
        Assert.Equal(
            "{controls:Inset Top=Xs, Left=Sm, Bottom=Xs}",
            AttributeValue(provisionalReasoningBody, "Margin"));
        Assert.Equal(
            "{controls:Inset Left=Md}",
            AttributeValue(provisionalReasoningBody, "Padding"));
        var reasoningHover = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "ToggleButton.ReasoningTraceDisclosure:pointerover");
        Assert.Contains(
            reasoningHover.Elements(),
            element => AttributeValue(element, "Property") == "Background"
                && AttributeValue(element, "Value") == "Transparent");
        var messages = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && AttributeValue(element, "ItemsSource")
                    == "{Binding AgentChat.Messages}");
        Assert.Contains(
            messages.Descendants(),
            element => element.Name.LocalName == "Setter"
                && AttributeValue(element, "Property")
                    == "HorizontalContentAlignment"
                && AttributeValue(element, "Value") == "Stretch");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "MarkdownPreviewView"
                && AttributeValue(element, "Text") == "{Binding Content}");
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && (AttributeValue(element, "Text") == "{Binding Content}"
                    || AttributeValue(element, "Text")
                        == "{Binding ReasoningSummaryDisplay}"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Click") == "OnCopyAgentMessageClick");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Click") == "OnForkAgentConversationClick"
                && AttributeValue(element, "IsVisible") == "{Binding CanFork}");
    }

    [Fact]
    public void Model_picker_keeps_filter_content_reasoning_and_speed_in_separate_bands()
    {
        var root = Assert.IsType<XElement>(LoadView().Root);
        var picker = FindNamedElement(root, "AgentModelPickerButton");
        var flyout = Assert.Single(
            picker.Descendants(),
            element => element.Name.LocalName == "Flyout");
        Assert.Equal("AgentModelMenu", AttributeValue(flyout, "FlyoutPresenterClasses"));

        var layout = Assert.Single(
            flyout.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,*,Auto", AttributeValue(layout, "RowDefinitions"));

        var filter = Assert.Single(
            layout.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && AttributeValue(element, "PlaceholderText") == "Filter models");
        Assert.Equal(
            "{Binding AgentChat.ModelSearch, Mode=TwoWay}",
            AttributeValue(filter, "Text"));

        var modelList = Assert.Single(
            layout.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && AttributeValue(element, "ItemsSource")
                    == "{Binding AgentChat.FilteredModels}");
        Assert.Equal(
            "1",
            AttributeValue(modelList.Ancestors().First(element =>
                element.Name.LocalName == "ScrollViewer"), "Grid.Row"));
        Assert.Contains(
            modelList.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "Text") == "{Binding ProviderName}");
        Assert.DoesNotContain(
            modelList.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "Text") == "{Binding Id}");
        var favorite = Assert.Single(
            modelList.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Click")
                    == "OnToggleFavoriteModelClick");
        Assert.Equal("{Binding}", AttributeValue(favorite, "Tag"));
        Assert.Equal(
            "{Binding FavoriteAccessibleName}",
            AttributeValue(favorite, "AutomationProperties.Name"));

        var footer = Assert.Single(
            layout.Elements(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "Grid.Row") == "2");
        Assert.Equal("0,1,0,0", AttributeValue(footer, "BorderThickness"));
        var reasoning = Assert.Single(
            footer.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && AttributeValue(element, "AutomationProperties.Name")
                    == "AI reasoning effort");
        Assert.Equal("1", AttributeValue(reasoning, "Grid.Column"));
        var serviceTier = Assert.Single(
            footer.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && AttributeValue(element, "AutomationProperties.Name")
                    == "AI service tier");
        Assert.Equal("1", AttributeValue(serviceTier, "Grid.Column"));
        Assert.Equal(
            "{Binding AgentChat.HasServiceTiers}",
            AttributeValue(
                serviceTier.Ancestors().First(element => element.Name.LocalName == "Grid"),
                "IsVisible"));
    }

    [Fact]
    public void Conversation_picker_uses_the_same_filter_list_footer_structure()
    {
        var root = Assert.IsType<XElement>(LoadView().Root);
        var picker = FindNamedElement(root, "AgentConversationHistoryButton");
        var flyout = Assert.Single(
            picker.Descendants(),
            element => element.Name.LocalName == "Flyout");
        Assert.Equal(
            "AgentConversationMenu",
            AttributeValue(flyout, "FlyoutPresenterClasses"));

        var layout = Assert.Single(
            flyout.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,*,Auto", AttributeValue(layout, "RowDefinitions"));

        var filter = Assert.Single(
            layout.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && AttributeValue(element, "PlaceholderText")
                    == "Filter conversations");
        Assert.Equal(
            "{Binding AgentChat.ConversationSearch, Mode=TwoWay}",
            AttributeValue(filter, "Text"));

        var conversations = Assert.Single(
            layout.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && AttributeValue(element, "ItemsSource")
                    == "{Binding AgentChat.FilteredConversations}");
        Assert.Contains(
            conversations.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && AttributeValue(element, "Text") == "{Binding Details}");

        var footer = Assert.Single(
            layout.Elements(),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "Grid.Row") == "2");
        Assert.Equal("0,1,0,0", AttributeValue(footer, "BorderThickness"));
        Assert.Contains(
            footer.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Click")
                    == "OnStartNewConversationClick");
    }

    [Fact]
    public void Full_access_is_a_normal_run_scoped_terminal_option()
    {
        var root = Assert.IsType<XElement>(LoadView().Root);
        var fullAccess = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && AttributeValue(element, "Content")
                    == "Full access for terminal actions");

        Assert.Null(AttributeValue(fullAccess, "IsEnabled"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => (AttributeValue(element, "Text") ?? string.Empty)
                .Contains("exact terminal scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Macos_live_regions_always_have_stable_non_null_names()
    {
        var root = Assert.IsType<XElement>(LoadView().Root);
        var liveRegions = root.Descendants().Where(element =>
            AttributeValue(element, "AutomationProperties.LiveSetting") is not null);

        Assert.NotEmpty(liveRegions);
        Assert.All(
            liveRegions,
            element =>
            {
                var name = AttributeValue(element, "AutomationProperties.Name");
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.DoesNotContain("{Binding", name, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Agent_workspace_forwards_original_input_without_owning_policy()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class AgentWorkspaceView");

        foreach (var interaction in Interactions.Keys)
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
        Assert.Contains(
            "private static void OnAgentChatTranscriptScrollChanged(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(transcript.ScrollToEnd);",
            codeBehind,
            StringComparison.Ordinal);

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_floatingWidth", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_floatingHeight", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_dockedWidth", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "PreserveSizeAcrossPresentationChanges();",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_workspace_markup_routes_every_interaction_to_its_local_relay()
    {
        var root = Assert.IsType<XElement>(LoadView().Root);

        foreach (var (interaction, handler) in Interactions)
        {
            Assert.Contains(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, InteractionAttribute(interaction)),
                    handler,
                    StringComparison.Ordinal));
        }
    }

    private static readonly string[] NamedControls =
    [
        "AgentChatTranscript",
        "AgentComposerToolbar",
        "AgentContextInspector",
        "AgentCurrentProgress",
        "AgentRunAudit",
        "AgentChatPromptInput",
    ];

    private static string InteractionAttribute(string interaction) =>
        interaction switch
        {
            "AgentQuestionResponseKeyDownRequested" => "ResponseKeyDownRequested",
            "ApproveAgentActionRequested" => "ApproveRequested",
            "DeclineAgentQuestionRequested" => "DeclineRequested",
            "DenyAgentActionRequested" => "DenyRequested",
            "EnableAgentCapabilityAskRequested" => "EnableAskRequested",
            "KeepAgentCapabilityOffRequested" => "KeepOffRequested",
            "SubmitAgentQuestionRequested" => "SubmitRequested",
            _ => "Click",
        };

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

    private static XDocument LoadView() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "AgentWorkspaceView.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
