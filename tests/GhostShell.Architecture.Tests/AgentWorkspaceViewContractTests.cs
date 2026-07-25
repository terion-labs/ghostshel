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

        var panel = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "AgentPanel"));
        var layout = Assert.Single(
            panel.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,Auto,*,Auto,Auto", AttributeValue(layout, "RowDefinitions"));

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
