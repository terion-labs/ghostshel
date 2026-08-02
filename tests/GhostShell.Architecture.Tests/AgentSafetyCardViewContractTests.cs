using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class AgentSafetyCardViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Agent_workspace_composes_distinct_question_and_capability_components()
    {
        var workspace = LoadView("AgentWorkspaceView");
        var question = Assert.Single(
            workspace.Descendants(),
            element => element.Name.LocalName == "AgentQuestionCardView");
        var capability = Assert.Single(
            workspace.Descendants(),
            element => element.Name.LocalName == "AgentCapabilityRequestCardView");

        Assert.Equal(
            "OnAgentQuestionResponseKeyDown",
            AttributeValue(question, "ResponseKeyDownRequested"));
        Assert.Equal(
            "OnDeclineAgentQuestionClick",
            AttributeValue(question, "DeclineRequested"));
        Assert.Equal(
            "OnSubmitAgentQuestionClick",
            AttributeValue(question, "SubmitRequested"));
        Assert.Equal(
            "OnEnableAgentCapabilityAskClick",
            AttributeValue(capability, "EnableAskRequested"));
        Assert.Equal(
            "OnKeepAgentCapabilityOffClick",
            AttributeValue(capability, "KeepOffRequested"));
    }

    [Fact]
    public void Question_component_preserves_untrusted_copy_and_non_approval_order()
    {
        var document = LoadComponent("AgentQuestionCardView");
        var card = FindNamedElement(document, "AgentPendingQuestion");

        Assert.Equal(
            "{Binding AgentChat.HasPendingQuestion}",
            AttributeValue(card, "IsVisible"));
        Assert.Equal(
            "Assertive",
            AttributeValue(card, "AutomationProperties.LiveSetting"));
        Assert.Contains(
            card.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Untrusted model question",
                StringComparison.Ordinal));
        Assert.Contains(
            card.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Content"),
                "Not approval",
                StringComparison.Ordinal));

        var answer = FindNamedElement(document, "AgentQuestionResponseInput");
        Assert.Equal("False", AttributeValue(answer, "AcceptsReturn"));
        Assert.Equal("2048", AttributeValue(answer, "MaxLength"));
        Assert.Equal("0", AttributeValue(answer, "TabIndex"));
        Assert.Equal(
            "OnAgentQuestionResponseKeyDown",
            AttributeValue(answer, "KeyDown"));
        Assert.Equal(
            "1",
            AttributeValue(FindButton(document, "Skip / decline"), "TabIndex"));
        Assert.Equal(
            "2",
            AttributeValue(FindButton(document, "Send answer"), "TabIndex"));
    }

    [Fact]
    public void Capability_component_preserves_run_local_scope_and_decision_order()
    {
        var document = LoadComponent("AgentCapabilityRequestCardView");
        var card = FindNamedElement(document, "AgentPendingCapabilityRequest");

        Assert.Equal(
            "{Binding AgentChat.HasPendingCapabilityRequest}",
            AttributeValue(card, "IsVisible"));
        Assert.Equal(
            "Assertive",
            AttributeValue(card, "AutomationProperties.LiveSetting"));
        var serialized = card.ToString();
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.ExactTarget}",
            serialized,
            StringComparison.Ordinal);
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.GrantWarning}",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingApproval",
            serialized,
            StringComparison.Ordinal);

        Assert.Equal(
            "0",
            AttributeValue(FindButton(document, "Keep Off"), "TabIndex"));
        Assert.Equal(
            "1",
            AttributeValue(
                FindButton(document, "Enable Ask for this run"),
                "TabIndex"));
    }

    [Fact]
    public void Safety_components_only_forward_original_decision_input()
    {
        AssertPassiveRelay(
            "public sealed partial class AgentQuestionCardView",
            "ResponseKeyDownRequested?.Invoke(sender, e);",
            "DeclineRequested?.Invoke(sender, e);",
            "SubmitRequested?.Invoke(sender, e);");
        AssertPassiveRelay(
            "public sealed partial class AgentCapabilityRequestCardView",
            "EnableAskRequested?.Invoke(sender, e);",
            "KeepOffRequested?.Invoke(sender, e);");
    }

    private static void AssertPassiveRelay(string typeMarker, params string[] relays)
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(typeMarker);
        foreach (var relay in relays)
        {
            Assert.Contains(relay, codeBehind, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
    }

    private static XElement FindButton(XDocument document, string content) =>
        Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    content,
                    StringComparison.Ordinal));

    private static XElement FindNamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string component) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            $"{component}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
