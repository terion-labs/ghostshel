using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using GhostShell.Agent;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime.Tests;

public sealed class TerminalAgentToolParserTests
{
    [Theory]
    [InlineData(BuiltInAgentTools.TerminalReadScreen, "{}", typeof(TerminalAgentIntent.ReadScreen))]
    [InlineData(BuiltInAgentTools.TerminalSendText, "{\"text\":\"menu\"}", typeof(TerminalAgentIntent.SendText))]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"first\\n\\tsecond\"}", typeof(TerminalAgentIntent.Paste))]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"enter\",\"modifiers\":[\"control\"]}", typeof(TerminalAgentIntent.SendKey))]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":\"control\"}", typeof(TerminalAgentIntent.SendChord))]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":4,\"row\":7}", typeof(TerminalAgentIntent.SendMouse))]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"text\":\"Selected\",\"timeout_ms\":5000}", typeof(TerminalAgentIntent.WaitForText))]
    [InlineData(BuiltInAgentTools.TerminalInterrupt, "{}", typeof(TerminalAgentIntent.Interrupt))]
    public async Task ParsesTheClosedTerminalIntentSet(
        string toolName,
        string arguments,
        Type expectedType)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.IsType(expectedType, parsed.Intent);
    }

    [Theory]
    [InlineData(BuiltInAgentTools.TerminalReadScreen, "{\"extra\":true}")]
    [InlineData(BuiltInAgentTools.TerminalSendText, "{\"text\":\"line\\nfeed\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"\\u0000\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"\\u001b[31m\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"safe\",\"extra\":true}")]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"a\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"enter\",\"modifiers\":[\"control\",\"control\"]}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"D\",\"modifier\":\"control\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":[\"control\",\"alt\"]}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":\"control\",\"text\":\"\\u0004\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"click\",\"column\":4,\"row\":7}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":-1,\"row\":7}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":4,\"row\":1000001}")]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"text\":\"ready\",\"timeout_ms\":30001}")]
    [InlineData("terminal.provider_extension", "{}")]
    public async Task RejectsUnknownOrUnboundedArguments(
        string toolName,
        string arguments)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.Contains(
            rejected.StableCode,
            new[] { "invalid_tool_arguments", "unknown_tool" });
    }

    [Fact]
    public async Task NativeKernelRejectsDuplicateJsonBeforeRuntimeParsing()
    {
        var session = new NativeAgentSession(new AgentRunId("run-1"));

        var result = await session.RunTurnAsync(
            "Use the tool.",
            [Tool(BuiltInAgentTools.TerminalSendText)],
            new ToolProvider(
                BuiltInAgentTools.TerminalSendText,
                "{\"text\":\"one\",\"text\":\"two\"}"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            result.ErrorCode);
        Assert.Empty(result.ToolProposals);
    }

    [Fact]
    public void ManagedTerminalGetsReadWaitAndMutationTools()
    {
        var tools = TerminalAgentToolSet.For(ContextPanel(
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalInterrupt));

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalWait,
                BuiltInAgentTools.TerminalSendText,
                BuiltInAgentTools.TerminalPaste,
                BuiltInAgentTools.TerminalSendKeys,
                BuiltInAgentTools.TerminalSendMouse,
                BuiltInAgentTools.TerminalInterrupt,
            ],
            tools.Select(tool => tool.Name));
        Assert.All(
            tools,
            tool =>
            {
                Assert.False(
                    tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
                Assert.DoesNotContain(
                    "session",
                    tool.InputSchema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "lease",
                    tool.InputSchema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
            });
        Assert.True(TerminalAgentToolSet.SupportsMutations(ContextPanel(
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalWrite)));
    }

    [Fact]
    public void NativeTerminalWithPhysicalInputBarrierGetsMutationTools()
    {
        var panel = ContextPanel(
            SessionCapabilities.NativeRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalInterrupt);

        var tools = TerminalAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalWait,
                BuiltInAgentTools.TerminalSendText,
                BuiltInAgentTools.TerminalPaste,
                BuiltInAgentTools.TerminalSendKeys,
                BuiltInAgentTools.TerminalSendMouse,
                BuiltInAgentTools.TerminalInterrupt,
            ],
            tools.Select(tool => tool.Name));
        Assert.True(TerminalAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public void RendererWithoutPhysicalInputBarrierFailsClosedToReadOnly()
    {
        var panel = ContextPanel(
            SessionCapabilities.NativeRenderer,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite);

        var tools = TerminalAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalWait,
            ],
            tools.Select(tool => tool.Name));
        Assert.False(TerminalAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public async Task Paste_preserves_approved_line_breaks_and_tabs_but_is_utf8_bounded()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalPaste,
            "{\"text\":\"first\\r\\n\\tsecond\"}");

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        Assert.Equal(
            "first\r\n\tsecond",
            Assert.IsType<TerminalAgentIntent.Paste>(parsed.Intent).Text);

        var oversized = await ProposalAsync(
            BuiltInAgentTools.TerminalPaste,
            $"{{\"text\":\"{new string('é', 1_025)}\"}}");
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(oversized));
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void Paste_requires_both_the_physical_input_barrier_and_paste_capability()
    {
        var eligible = ContextPanel(
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalPaste);
        var noBarrier = ContextPanel(SessionCapabilities.TerminalPaste);
        var noPaste = ContextPanel(SessionCapabilities.TerminalAgentInputBarrier);

        Assert.Contains(
            TerminalAgentToolSet.For(eligible),
            tool => tool.Name == BuiltInAgentTools.TerminalPaste);
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noBarrier),
            tool => tool.Name == BuiltInAgentTools.TerminalPaste);
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noPaste),
            tool => tool.Name == BuiltInAgentTools.TerminalPaste);
        Assert.True(TerminalAgentToolSet.SupportsMutations(eligible));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noBarrier));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noPaste));
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("run-1"));
        var result = await session.RunTurnAsync(
            "Use the tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(params string[] capabilities)
    {
        var sessionId = new SessionId("session-1");
        var windowId = new WindowInstanceId("window-1");
        var workspaceId = new WorkspaceInstanceId("workspace-1");
        var tabId = new TabInstanceId("tab-1");
        var panelId = new PanelInstanceId("panel-1");
        var panel = new PanelInstance(
            panelId,
            PanelKind.Terminal,
            "Production",
            sessionId);
        var tab = new TabInstance(tabId, "Shells", [panel], panelId);
        var workspace = new WorkspaceInstance(
            workspaceId,
            "Operations",
            [tab],
            tabId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            workspace,
            revision: 3,
            lastSequence: 3);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Terminal,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(capabilities),
            Revision: 5,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private sealed class ToolProvider(string name, string arguments) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.ToolCallStarted(
                0,
                "call-1",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
