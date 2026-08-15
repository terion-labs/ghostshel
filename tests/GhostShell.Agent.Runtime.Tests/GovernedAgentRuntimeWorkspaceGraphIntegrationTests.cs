using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;

namespace GhostShell.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    private static readonly string[] WorkspaceGraphToolNames =
    [
        BuiltInAgentTools.WorkspaceList,
        BuiltInAgentTools.WorkspaceInspect,
        BuiltInAgentTools.TabList,
        BuiltInAgentTools.PanelList,
    ];

    [Fact]
    public async Task GraphBackedOpenTabProjectsNonSessionPanelsAndContinuesIntoTerminalTool()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceInspect,
                "{}"),
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.TerminalReadScreen,
                """
                {"panel_id":"workspace-graph-terminal"}
                """),
            WorkspaceGraphProviderRound.Answer("Inspection complete."));
        var policy = ExactWorkspaceGraphPolicy(AgentPermission.Auto);
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                policy);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect this tab and then read its terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var requests = provider.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(
            WorkspaceGraphToolNames,
            toolName => Assert.Contains(
                requests[0].Tools,
                tool => tool.Name == toolName));
        Assert.Contains(
            requests[0].Tools,
            tool => tool.Name == BuiltInAgentTools.TerminalReadScreen);

        var graphResult = ToolResult(requests[1], "workspace-graph-call-1");
        Assert.Equal("workspace_inspected", graphResult.StableCode);
        using var document = JsonDocument.Parse(graphResult.Value.Content);
        var root = document.RootElement;
        Assert.True(root.GetProperty("scope_limited").GetBoolean());
        Assert.Equal(
            "open_tab",
            root.GetProperty("scope_kind").GetString());
        var panels = root
            .GetProperty("workspace")
            .GetProperty("tabs")[0]
            .GetProperty("panels")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            [
                fixture.TerminalPanelId.Value,
                fixture.StatisticsPanelId.Value,
                fixture.ProcessPanelId.Value,
            ],
            panels
                .Select(panel => panel.GetProperty("panel_id").GetString()!)
                .ToArray());
        Assert.Equal(
            ["terminal", "statistics", "process_monitor"],
            panels
                .Select(panel => panel.GetProperty("kind").GetString()!)
                .ToArray());
        Assert.DoesNotContain(
            fixture.SiblingPanelId.Value,
            graphResult.Value.Content,
            StringComparison.Ordinal);

        var terminalResult = ToolResult(
            requests[2],
            "workspace-graph-call-2");
        Assert.Equal("tool_succeeded", terminalResult.StableCode);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        Assert.Equal(1, fixture.GraphHost.SuccessCount);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.WorkspaceInspect
                && auditEvent.Outcome == AuditOutcome.Succeeded);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.TerminalReadScreen
                && auditEvent.Outcome == AuditOutcome.Succeeded);

        var effectivePolicy = Assert.IsType<AgentPolicy>(
            fixture.Runtime.Snapshot.EffectivePolicy);
        Assert.Equal(policy.Provider, effectivePolicy.Provider);
        Assert.Equal(policy.Model, effectivePolicy.Model);
        Assert.Equal(
            policy.Model,
            fixture.ProviderResolver.Binding.RequestedModel);
    }

    [Fact]
    public async Task GraphToolsAreNotAdvertisedForGraphlessConnectionSession()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Answer("No graph requested."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphlessConnectionSession,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Describe the available terminal tools."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(provider.Requests);
        Assert.All(
            WorkspaceGraphToolNames,
            toolName => Assert.DoesNotContain(
                request.Tools,
                tool => tool.Name == toolName));
        Assert.Contains(
            request.Tools,
            tool => tool.Name == BuiltInAgentTools.TerminalReadScreen);
        Assert.Equal(0, fixture.GraphHost.CallCount);
    }

    [Fact]
    public async Task SupersededConnectionSessionCannotProjectCurrentPanelGraph()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceList,
                "{}"),
            WorkspaceGraphProviderRound.Answer(
                "The exact connection session was superseded."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedConnectionSession,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        provider.BeforeRoundAsync = round =>
            round == 1
                ? fixture.RelinkPanelToReplacementSessionAsync()
                : ValueTask.CompletedTask;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("List the graph for this exact connection session."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(
            provider.Requests.ToArray()[0].Tools,
            tool => tool.Name == BuiltInAgentTools.WorkspaceList);
        Assert.Equal(0, fixture.GraphHost.CallCount);
        var rejected = ToolResult(
            provider.Requests.ToArray()[1],
            "workspace-graph-call-1");
        Assert.Equal("target_changed", rejected.StableCode);
        Assert.Equal(AgentToolResultStatus.Failed, rejected.Status);
    }

    [Fact]
    public async Task SearchOffRejectsGraphObservationBeforeHostExecution()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceList,
                "{}"),
            WorkspaceGraphProviderRound.Answer("Search is disabled."));
        var policy = ExactWorkspaceGraphPolicy(AgentPermission.Off);
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                policy);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("List the workspace in this scope."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.GraphHost.CallCount);
        var denied = ToolResult(
            provider.Requests.ToArray()[1],
            "workspace-graph-call-1");
        Assert.Equal("policy_denied", denied.StableCode);
        Assert.Equal(
            AgentToolResultStatus.Failed,
            denied.Status);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.WorkspaceList
                && auditEvent.Outcome == AuditOutcome.Denied);
        Assert.Equal(
            policy.Model,
            fixture.ProviderResolver.Binding.RequestedModel);
    }

    [Fact]
    public async Task PresentationRefreshDoesNotInvalidateGraphObservation()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceInspect,
                "{}"),
            WorkspaceGraphProviderRound.Answer("Refreshed graph inspected."));
        var policy = ExactWorkspaceGraphPolicy(AgentPermission.Auto);
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                policy);
        provider.BeforeRoundAsync = round =>
            round == 1
                ? fixture.ReplaceGraphAsync(
                    WorkspaceGraphChange.PresentationRefresh)
                : ValueTask.CompletedTask;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the refreshed graph."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        var toolResult = ToolResult(
            provider.Requests.ToArray()[1],
            "workspace-graph-call-1");
        Assert.Equal("workspace_inspected", toolResult.StableCode);
        using var document = JsonDocument.Parse(toolResult.Value.Content);
        var workspace = document.RootElement.GetProperty("workspace");
        Assert.Equal(
            "Renamed workspace",
            workspace
                .GetProperty("title")
                .GetProperty("text")
                .GetString());
        Assert.False(workspace.TryGetProperty(
            "workspace_revision",
            out _));
        Assert.False(workspace.TryGetProperty(
            "graph_sequence",
            out _));
        var statistics = workspace
            .GetProperty("tabs")[0]
            .GetProperty("panels")
            .EnumerateArray()
            .Single(panel =>
                panel.GetProperty("panel_id").GetString()
                == fixture.StatisticsPanelId.Value);
        Assert.True(statistics.GetProperty("focused").GetBoolean());
    }

    [Theory]
    [InlineData(SessionLifecycle.Starting)]
    [InlineData(SessionLifecycle.Closing)]
    public async Task LifecycleRefreshAllowsGraphObservationButRejectsTerminalTool(
        SessionLifecycle lifecycle)
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceList,
                "{}"),
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.TerminalReadScreen,
                "{}"),
            WorkspaceGraphProviderRound.Answer(
                "Only graph metadata remained available."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        provider.BeforeRoundAsync = round =>
        {
            if (round == 1)
            {
                fixture.ContextProxy.LifecycleOverride = lifecycle;
            }

            return ValueTask.CompletedTask;
        };

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the workspace, then read the terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        var requests = provider.Requests.ToArray();
        Assert.Equal(
            "workspaces_listed",
            ToolResult(
                requests[1],
                "workspace-graph-call-1").StableCode);
        var terminalResult = ToolResult(
            requests[2],
            "workspace-graph-call-2");
        Assert.Equal("target_changed", terminalResult.StableCode);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.TerminalReadScreen);
    }

    [Fact]
    public async Task OutOfScopePanelReorderDoesNotInvalidateOrWidenExactPanelScope()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceInspect,
                "{}"),
            WorkspaceGraphProviderRound.Answer(
                "The exact panel scope remained stable."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedExactPanel,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        provider.BeforeRoundAsync = round =>
            round == 1
                ? fixture.ReplaceGraphAsync(
                    WorkspaceGraphChange.PanelsReordered)
                : ValueTask.CompletedTask;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect only this exact panel."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        var graphResult = ToolResult(
            provider.Requests.ToArray()[1],
            "workspace-graph-call-1");
        Assert.Equal("workspace_inspected", graphResult.StableCode);
        Assert.Contains(
            fixture.TerminalPanelId.Value,
            graphResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.StatisticsPanelId.Value,
            graphResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.ProcessPanelId.Value,
            graphResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"workspace_revision\"",
            graphResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"graph_sequence\"",
            graphResult.Value.Content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkspaceGraphChange.PanelAdded)]
    [InlineData(WorkspaceGraphChange.PanelRemoved)]
    [InlineData(WorkspaceGraphChange.PanelsReordered)]
    [InlineData(WorkspaceGraphChange.PanelKindChanged)]
    public async Task BroadScopeStructuralDriftRefreshesBeforeHostProjection(
        WorkspaceGraphChange change)
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceInspect,
                "{}"),
            WorkspaceGraphProviderRound.Answer("The graph changed."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        provider.BeforeRoundAsync = round =>
            round == 1
                ? fixture.ReplaceGraphAsync(change)
                : ValueTask.CompletedTask;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the original graph."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        var inspected = ToolResult(
            provider.Requests.ToArray()[1],
            "workspace-graph-call-1");
        Assert.Equal("workspace_inspected", inspected.StableCode);
        Assert.Equal(
            AgentToolResultStatus.Succeeded,
            inspected.Status);
    }

    [Fact]
    public async Task BroadScopeRepinsAChangedGraphOnTheNextTurn()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(
            WorkspaceGraphProviderRound.Answer("Initial graph pinned."),
            WorkspaceGraphProviderRound.Tool(
                BuiltInAgentTools.WorkspaceInspect,
                "{}"),
            WorkspaceGraphProviderRound.Answer("Changed graph inspected."));
        await using var fixture =
            await WorkspaceGraphRuntimeFixture.CreateAsync(
                provider,
                WorkspaceGraphFixtureKind.GraphBackedOpenTab,
                ExactWorkspaceGraphPolicy(AgentPermission.Auto));

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Pin this graph."),
            CancellationToken.None)).IsSuccess);
        await fixture.ReplaceGraphAsync(WorkspaceGraphChange.PanelAdded);

        var refreshed = await fixture.Runtime.SendAsync(
            fixture.Prompt("Reuse the old run."),
            CancellationToken.None);

        Assert.True(refreshed.IsSuccess);
        Assert.Equal(1, fixture.GraphHost.CallCount);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(
            "workspace_inspected",
            ToolResult(
                provider.Requests.ToArray()[2],
                "workspace-graph-call-2").StableCode);
    }

    private static AgentToolResult ToolResult(
        AgentProviderRequest request,
        string providerCallId) =>
        Assert.Single(
            request.Messages,
            message =>
                message.Role == AgentMessageRole.Tool
                && message.ToolResult?.ProviderCallId == providerCallId)
            .ToolResult!;

    private static AgentPolicy ExactWorkspaceGraphPolicy(
        AgentPermission searchPermission) =>
        AgentPolicy.Default with
        {
            Provider = "provider-1",
            Model = "workspace-graph-model",
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.Search,
                searchPermission),
        };

    public enum WorkspaceGraphChange
    {
        PresentationRefresh,
        PanelAdded,
        PanelRemoved,
        PanelsReordered,
        PanelKindChanged,
    }

    private enum WorkspaceGraphFixtureKind
    {
        GraphBackedOpenTab,
        GraphBackedExactPanel,
        GraphBackedConnectionSession,
        GraphlessConnectionSession,
    }

    private sealed record WorkspaceGraphProviderRound(
        string? ToolName,
        string Arguments,
        string AnswerText)
    {
        public static WorkspaceGraphProviderRound Tool(
            string toolName,
            string arguments) =>
            new(toolName, arguments, string.Empty);

        public static WorkspaceGraphProviderRound Answer(string answer) =>
            new(null, "{}", answer);
    }

    private sealed class ScriptedWorkspaceGraphProvider(
        IReadOnlyList<WorkspaceGraphProviderRound> rounds)
        : IAgentProvider
    {
        private int _callCount;

        public ConcurrentQueue<AgentProviderRequest> Requests { get; } = [];

        public Func<int, ValueTask>? BeforeRoundAsync { get; set; }

        public static ScriptedWorkspaceGraphProvider Create(
            params WorkspaceGraphProviderRound[] rounds) =>
            new(rounds);

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _callCount);
            var round = rounds.ElementAtOrDefault(call - 1)
                ?? throw new InvalidOperationException(
                    "The workspace graph provider received an unexpected round.");
            if (BeforeRoundAsync is { } beforeRound)
            {
                await beforeRound(call).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            if (round.ToolName is { } toolName)
            {
                yield return new AgentProviderEvent.ToolCallStarted(
                    0,
                    $"workspace-graph-call-{call}",
                    ProviderToolName.FromInternal(toolName));
                yield return new AgentProviderEvent.ToolCallArgumentsDelta(
                    0,
                    round.Arguments);
                yield return new AgentProviderEvent.ToolCallCompleted(0);
                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse);
            }
            else
            {
                yield return new AgentProviderEvent.TextDelta(round.AnswerText);
                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn);
            }

            await Task.Yield();
        }
    }

    private sealed class WorkspaceGraphRuntimeFixture : IAsyncDisposable
    {
        private static readonly CapabilitySet AttachmentCapabilities = new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.AttachInteractive,
            SessionCapabilities.InputLease,
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalFocus,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalEnter,
            SessionCapabilities.TerminalInterrupt,
            SessionCapabilities.TerminalWait,
        ]);

        private readonly WorkspaceGraphFixtureKind _kind;
        private readonly AgentPolicy _policy;

        private WorkspaceGraphRuntimeFixture(
            ScriptedWorkspaceGraphProvider provider,
            WorkspaceGraphFixtureKind kind,
            AgentPolicy policy)
        {
            _kind = kind;
            _policy = policy;
            Audit = new RecordingAuditStore();
            Broker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                Audit,
                TimeProvider.System);
            var terminalComposer = new AgentTerminalActionComposer();
            var browserComposer = new AgentBrowserActionComposer();
            var fileComposer = new AgentFileActionComposer();
            var panelComposer = new AgentPanelActionComposer();
            var graphComposer = new AgentWorkspaceGraphActionComposer();
            Client = new InMemorySessionHostClient(
                new WorkspaceGraphTerminalFactory(),
                new DesktopLifecyclePolicy(),
                TimeProvider.System,
                agentActionComposer: terminalComposer,
                agentBrowserActionComposer: browserComposer,
                agentAuthorizationConsumer: Broker,
                agentFileActionComposer: fileComposer,
                agentPanelActionComposer: panelComposer,
                agentWorkspaceGraphActionComposer: graphComposer);
            var contextClient = DispatchProxy.Create<
                ISessionHostClient,
                WorkspaceGraphContextProxy>();
            ContextProxy =
                (WorkspaceGraphContextProxy)(object)contextClient;
            ContextProxy.Initialize(Client);
            GraphHost = new CountingWorkspaceGraphHost(Client);
            ProviderResolver = new FixedProviderResolver(provider);
            Runtime = new GovernedAgentRuntime(
                contextClient,
                Broker,
                Client,
                Client,
                Client,
                Client,
                terminalComposer,
                browserComposer,
                fileComposer,
                panelComposer,
                BuiltInAgentTools.Catalog,
                ProviderResolver,
                new TestApprovalPrincipal(ClientId),
                TimeProvider.System,
                GraphHost,
                graphComposer);
        }

        public WindowInstanceId WindowId { get; } =
            new("workspace-graph-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("workspace-graph-workspace");

        public TabInstanceId TabId { get; } =
            new("workspace-graph-primary-tab");

        public TabInstanceId SiblingTabId { get; } =
            new("workspace-graph-sibling-tab");

        public PanelInstanceId TerminalPanelId { get; } =
            new("workspace-graph-terminal");

        public PanelInstanceId StatisticsPanelId { get; } =
            new("workspace-graph-statistics");

        public PanelInstanceId ProcessPanelId { get; } =
            new("workspace-graph-process");

        public PanelInstanceId SiblingPanelId { get; } =
            new("workspace-graph-sibling");

        public PanelInstanceId AddedPanelId { get; } =
            new("workspace-graph-added");

        public SessionId SessionId { get; } =
            new("workspace-graph-session");

        public SessionId ReplacementSessionId { get; } =
            new("workspace-graph-replacement-session");

        public ClientId ClientId { get; } =
            new("workspace-graph-client");

        public RecordingAuditStore Audit { get; }

        public AgentCapabilityBroker Broker { get; }

        public InMemorySessionHostClient Client { get; }

        public CountingWorkspaceGraphHost GraphHost { get; }

        public WorkspaceGraphContextProxy ContextProxy { get; }

        public FixedProviderResolver ProviderResolver { get; }

        public GovernedAgentRuntime Runtime { get; }

        public WorkspaceGraphSnapshot? InitialGraph { get; private set; }

        public static async ValueTask<WorkspaceGraphRuntimeFixture> CreateAsync(
            ScriptedWorkspaceGraphProvider provider,
            WorkspaceGraphFixtureKind kind,
            AgentPolicy policy)
        {
            var fixture = new WorkspaceGraphRuntimeFixture(
                provider,
                kind,
                policy);
            try
            {
                await fixture.InitializeAsync().ConfigureAwait(false);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public GovernedAgentPrompt Prompt(string message) =>
            new(
                ProviderResolver.Binding.ProfileId,
                message,
                _kind switch
                {
                    WorkspaceGraphFixtureKind.GraphBackedOpenTab =>
                        new AgentTarget.OpenTab(
                        WindowId,
                        WorkspaceId,
                        TabId),
                    WorkspaceGraphFixtureKind.GraphBackedExactPanel =>
                        new AgentTarget.Panel(
                            WindowId,
                            WorkspaceId,
                            TabId,
                            TerminalPanelId),
                    WorkspaceGraphFixtureKind.GraphBackedConnectionSession
                        or WorkspaceGraphFixtureKind.GraphlessConnectionSession =>
                        new AgentTarget.ConnectionSession(SessionId),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(_kind)),
                },
                _policy);

        public async ValueTask ReplaceGraphAsync(
            WorkspaceGraphChange change)
        {
            if (_kind == WorkspaceGraphFixtureKind.GraphlessConnectionSession)
            {
                throw new InvalidOperationException(
                    "A graphless fixture cannot replace a workspace graph.");
            }

            var current = Value(await Client.GetWorkspaceGraphAsync(
                    WorkspaceId,
                    HumanContext(),
                    CancellationToken.None)
                .ConfigureAwait(false));
            _ = Value(await Client.RegisterWorkspaceGraphAsync(
                    new RegisterWorkspaceGraphRequest(
                        WindowId,
                        Workspace(change)),
                    HumanContext(current.Revision),
                    CancellationToken.None)
                .ConfigureAwait(false));
        }

        public async ValueTask RelinkPanelToReplacementSessionAsync()
        {
            if (_kind
                != WorkspaceGraphFixtureKind.GraphBackedConnectionSession)
            {
                throw new InvalidOperationException(
                    "Only a graph-backed exact session fixture can relink its panel.");
            }

            await OpenAndActivateSessionAsync(ReplacementSessionId)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync().ConfigureAwait(false);
            await Client.DisposeAsync().ConfigureAwait(false);
            await Broker.DisposeAsync().ConfigureAwait(false);
        }

        private async ValueTask InitializeAsync()
        {
            if (_kind != WorkspaceGraphFixtureKind.GraphlessConnectionSession)
            {
                _ = Value(await Client.RegisterWorkspaceGraphAsync(
                        new RegisterWorkspaceGraphRequest(
                            WindowId,
                            Workspace(change: null)),
                        HumanContext(),
                        CancellationToken.None)
                    .ConfigureAwait(false));
            }

            await OpenAndActivateSessionAsync(SessionId)
                .ConfigureAwait(false);
            if (_kind != WorkspaceGraphFixtureKind.GraphlessConnectionSession)
            {
                InitialGraph = Value(await Client.GetWorkspaceGraphAsync(
                        WorkspaceId,
                        HumanContext(),
                        CancellationToken.None)
                    .ConfigureAwait(false));
            }
        }

        private async ValueTask OpenAndActivateSessionAsync(
            SessionId sessionId)
        {
            _ = Value(await Client.EnsureTerminalSessionAsync(
                    new EnsureTerminalSessionRequest(
                        sessionId,
                        new SessionOwner(
                            HostMode.Desktop,
                            WindowId,
                            WorkspaceId,
                            TabId,
                            TerminalPanelId),
                        "Workspace graph terminal",
                        new TerminalLaunchRequest(
                            Environment.CurrentDirectory)),
                    HumanContext(),
                    CancellationToken.None)
                .ConfigureAwait(false));
            var attachment = Value(await Client.AttachAsync(
                    new AttachSessionRequest(
                        sessionId,
                        ClientId,
                        AttachmentKind.Interactive,
                        new ViewportDescriptor(800, 600, 2),
                        AttachmentCapabilities),
                    HumanContext(),
                    CancellationToken.None)
                .ConfigureAwait(false));
            _ = Value(await Client.AttachTerminalRendererAsync(
                    new AttachTerminalRendererRequest(
                        sessionId,
                        attachment.Attachment.Id,
                        new NativeRendererHost(
                            "GhostShell.Managed",
                            0,
                            new ViewportDescriptor(800, 600, 2))),
                    HumanContext(),
                    CancellationToken.None)
                .ConfigureAwait(false));
            var lease = Value(await Client.AcquireInputLeaseAsync(
                    new AcquireInputLeaseRequest(
                        sessionId,
                        attachment.Attachment.Id,
                        TimeSpan.FromMinutes(5)),
                    HumanContext(),
                    CancellationToken.None)
                .ConfigureAwait(false));
            Assert.True(lease.Granted);
        }

        private WorkspaceInstance Workspace(
            WorkspaceGraphChange? change)
        {
            var terminal = new PanelInstance(
                TerminalPanelId,
                PanelKind.Terminal,
                change == WorkspaceGraphChange.PresentationRefresh
                    ? "Renamed terminal"
                    : "Terminal");
            var statistics = new PanelInstance(
                StatisticsPanelId,
                PanelKind.Statistics,
                "Statistics");
            var process = new PanelInstance(
                ProcessPanelId,
                change == WorkspaceGraphChange.PanelKindChanged
                    ? PanelKind.Statistics
                    : PanelKind.ProcessMonitor,
                "Processes");
            IReadOnlyList<PanelInstance> primaryPanels = change switch
            {
                WorkspaceGraphChange.PanelAdded =>
                [
                    terminal,
                    statistics,
                    process,
                    new PanelInstance(
                        AddedPanelId,
                        PanelKind.Statistics,
                        "Added statistics"),
                ],
                WorkspaceGraphChange.PanelRemoved =>
                    [terminal, statistics],
                WorkspaceGraphChange.PanelsReordered =>
                    [statistics, terminal, process],
                _ => [terminal, statistics, process],
            };
            var primary = new TabInstance(
                TabId,
                change == WorkspaceGraphChange.PresentationRefresh
                    ? "Renamed primary"
                    : "Primary",
                primaryPanels,
                change == WorkspaceGraphChange.PresentationRefresh
                    ? StatisticsPanelId
                    : TerminalPanelId);
            var sibling = new PanelInstance(
                SiblingPanelId,
                PanelKind.Browser,
                "Out-of-scope browser");
            var siblingTab = new TabInstance(
                SiblingTabId,
                "Sibling",
                [sibling],
                sibling.Id);
            return new WorkspaceInstance(
                WorkspaceId,
                change == WorkspaceGraphChange.PresentationRefresh
                    ? "Renamed workspace"
                    : "Workspace",
                [primary, siblingTab],
                primary.Id);
        }

        private OperationContext HumanContext(
            long? expectedRevision = null) =>
            new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(ClientId.Value),
                    ActorKind.Human,
                    "Workspace graph test user",
                    ClientId),
                expectedRevision,
                CancellationId: CancellationId.New());

        private static T Value<T>(HostResult<T> result) =>
            Assert.IsType<HostResult<T>.Success>(result).Value;
    }

    public class WorkspaceGraphContextProxy : DispatchProxy
    {
        private ISessionHostClient _inner = null!;

        public SessionLifecycle? LifecycleOverride { get; set; }

        public void Initialize(ISessionHostClient inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new ArgumentNullException(nameof(targetMethod));
            }

            if (targetMethod.Name
                    == nameof(ISessionHostClient.InspectAgentContextAsync)
                && args is
                [
                    AgentContextRequest request,
                    OperationContext context,
                    CancellationToken cancellationToken,
                ])
            {
                return InspectAsync(
                    request,
                    context,
                    cancellationToken);
            }

            return targetMethod.Invoke(_inner, args);
        }

        private async ValueTask<HostResult<AgentContextSnapshot>>
            InspectAsync(
                AgentContextRequest request,
                OperationContext context,
                CancellationToken cancellationToken)
        {
            var result = await _inner.InspectAgentContextAsync(
                    request,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            if (LifecycleOverride is not { } lifecycle
                || result
                    is not HostResult<AgentContextSnapshot>.Success success)
            {
                return result;
            }

            return HostResult<AgentContextSnapshot>.Succeed(
                WithLifecycle(success.Value, lifecycle),
                success.ResultingRevision);
        }

        private static AgentContextSnapshot WithLifecycle(
            AgentContextSnapshot source,
            SessionLifecycle lifecycle)
        {
            var orderedPanels = source.Panels
                .OrderBy(panel => panel.GraphPanelOrder)
                .ToArray();
            if (source.Target is not AgentTarget.OpenTab
                || orderedPanels.Select(panel => panel.TabId).Distinct().Count()
                    != 1)
            {
                throw new InvalidOperationException(
                    "The lifecycle test projection expects one graph-backed tab.");
            }

            var graphPanels = orderedPanels
                .Select(panel => new PanelInstance(
                    panel.PanelId,
                    panel.Kind,
                    panel.PanelTitle ?? panel.PanelId.Value,
                    panel.SessionId))
                .ToArray();
            var focusedPanel = orderedPanels
                .SingleOrDefault(panel => panel.IsFocused);
            var tab = new TabInstance(
                orderedPanels[0].TabId,
                orderedPanels[0].TabTitle ?? orderedPanels[0].TabId.Value,
                graphPanels,
                focusedPanel?.PanelId ?? graphPanels[0].Id);
            var workspace = new WorkspaceInstance(
                orderedPanels[0].WorkspaceId,
                orderedPanels[0].WorkspaceTitle
                    ?? orderedPanels[0].WorkspaceId.Value,
                [tab],
                tab.Id);
            var graph = new WorkspaceGraphSnapshot(
                orderedPanels[0].WindowId,
                workspace,
                orderedPanels[0].WorkspaceRevision,
                orderedPanels[0].GraphSequence);
            var refreshedPanels = orderedPanels
                .Select(panel => AgentContextPanel.ForGraphPanel(
                    graph,
                    panel.TabId,
                    panel.PanelId,
                    panel.SessionId is null
                        ? null
                        : Session(panel, lifecycle)))
                .ToArray();
            return new AgentContextSnapshot(
                source.Target,
                refreshedPanels,
                source.CapturedAtUtc);
        }

        private static SessionDescriptor Session(
            AgentContextPanel panel,
            SessionLifecycle lifecycle) =>
            new(
                panel.SessionId
                    ?? throw new ArgumentException(
                        "A lifecycle refresh requires a live session.",
                        nameof(panel)),
                panel.Kind,
                lifecycle,
                panel.Health ?? SessionHealth.Healthy,
                new SessionOwner(
                    HostMode.Desktop,
                    panel.WindowId,
                    panel.WorkspaceId,
                    panel.TabId,
                    panel.PanelId),
                new CapabilitySet(panel.Capabilities),
                panel.SessionRevision ?? 0,
                panel.HasActiveWork,
                "Lifecycle refreshed",
                TerminalMetadata: panel.Kind == PanelKind.Terminal
                    ? new TerminalSessionMetadata(
                        panel.ConnectionId,
                        panel.ConnectionBoundary ?? "Local terminal",
                        panel.InitialWorkingDirectory,
                        panel.CurrentWorkingDirectory)
                    : null,
                FileMetadata: panel.FileMetadata,
                BrowserMetadata: panel.BrowserMetadata);
    }

    private sealed class WorkspaceGraphTerminalFactory
        : ITerminalSessionFactory
    {
        public CapabilitySet Capabilities { get; } =
            InteractiveTuiTerminalSession.SupportedCapabilities;

        public ValueTask<ITerminalPanelSession> CreateAsync(
            SessionId sessionId,
            TerminalLaunchRequest launch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ITerminalPanelSession>(
                new InteractiveTuiTerminalSession(sessionId, launch));
        }
    }

    private sealed class CountingWorkspaceGraphHost(
        IAgentWorkspaceGraphSessionHost inner)
        : IAgentWorkspaceGraphSessionHost
    {
        private int _callCount;
        private int _successCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public int SuccessCount => Volatile.Read(ref _successCount);

        public async ValueTask<HostResult<AgentWorkspaceGraphActionResult>>
            RunAgentWorkspaceGraphActionAsync(
                AgentAuthorizationId authorizationId,
                AgentWorkspaceGraphAction action,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var result = await inner.RunAgentWorkspaceGraphActionAsync(
                    authorizationId,
                    action,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result
                is HostResult<AgentWorkspaceGraphActionResult>.Success)
            {
                Interlocked.Increment(ref _successCount);
            }

            return result;
        }
    }
}
