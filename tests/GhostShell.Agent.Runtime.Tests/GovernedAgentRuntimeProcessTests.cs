using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeProcessTests
{
    [Fact]
    public async Task AutoProcessObservationUsesBrokerHostAndContinuesProvider()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                """{"sort":"memory_desc","limit":16}"""),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Processes.Snapshot = new ProcessMonitorSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                Entry(
                    41,
                    "api_key=secret-canary",
                    workingSetBytes: 1),
                Entry(
                    42,
                    "worker",
                    workingSetBytes: 2_048,
                    isGhostShell: true),
            ],
            EnumeratedProcessCount: 2,
            ObservedProcessCount: 2,
            IsTruncated: false);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Show the busiest local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        var action = Assert.Single(fixture.Processes.Actions);
        Assert.Equal(
            ProcessRuntimeContextProxy.ProcessPanelId,
            action.Request.PanelId);
        Assert.Equal(16, action.Request.Limit);
        Assert.Equal(
            ProcessMonitorSort.MemoryDescending,
            action.Request.Sort);
        Assert.Equal(1, fixture.Processes.CallCount);
        Assert.Equal(0, fixture.Terminal.CallCount);

        var initialRequest = fixture.Provider.Requests.ToArray()[0];
        var tool = Assert.Single(
            initialRequest.Tools,
            candidate => candidate.Name == BuiltInAgentTools.ProcessesList);
        Assert.DoesNotContain(
            "panel_id",
            tool.InputSchema.GetRawText(),
            StringComparison.Ordinal);
        var system = Assert.Single(
            initialRequest.Messages,
            message => message.Role == AgentMessageRole.System);
        Assert.Contains(
            "process_count=1",
            system.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "process names",
            system.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "untrusted",
            system.Content,
            StringComparison.OrdinalIgnoreCase);

        var contextItem = Assert.Single(
            fixture.Runtime.Snapshot.ContextItems);
        Assert.Equal(PanelKind.ProcessMonitor, contextItem.Kind);
        Assert.Contains(
            BuiltInAgentTools.ProcessesList,
            contextItem.SupportedOperations);

        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(
            AgentToolResultStatus.Succeeded,
            toolResult.Status);
        Assert.Equal("processes_listed", toolResult.StableCode);
        Assert.DoesNotContain(
            "secret-canary",
            toolResult.Value.Content,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(toolResult.Value.Content);
        Assert.Equal(
            ProcessAgentToolResultJson.ContentOrigin,
            document.RootElement
                .GetProperty("content_origin")
                .GetString());
        var rows = document.RootElement
            .GetProperty("processes")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("worker", rows[0].GetProperty("name").GetString());
        Assert.Equal(
            "[REDACTED PROCESS NAME]",
            rows[1].GetProperty("name").GetString());
        Assert.DoesNotContain(
            "total_processor_time",
            toolResult.Value.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "command_line",
            toolResult.Value.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Requested);
        var completedAudit = Assert.Single(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Succeeded);
        var completedDetails =
            Assert.IsType<AuditDetails.AgentActionDetails>(
                completedAudit.Details);
        Assert.Equal(
            AgentCapability.ProcessControl,
            completedDetails.Capability);
        Assert.Equal(2, completedDetails.Binding.ResultCount);
        Assert.Null(completedDetails.Binding.ArtifactReference);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "secret-canary",
                StringComparison.Ordinal));
        var auditJson = JsonSerializer.Serialize(fixture.Audit.Events);
        Assert.DoesNotContain(
            "secret-canary",
            auditJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "worker",
            auditJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskModePresentsExactLocalObservationAndNeverYieldsInput()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                """{"sort":"pid_asc","limit":64}"""),
            ProcessPolicy(AgentPermission.Ask));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("List local processes."),
            CancellationToken.None).AsTask();
        var approval = await WaitForApprovalAsync(fixture.Runtime);

        Assert.Equal(BuiltInAgentTools.ProcessesList, approval.ToolName);
        Assert.Equal(AgentPermission.Ask, approval.Permission);
        Assert.Equal(AgentActionRisk.Observation, approval.Risk);
        Assert.False(approval.TemporarilyYieldsTerminalInput);
        Assert.Equal(
            "Local Process Monitor",
            approval.Presentation.TargetTitle);
        Assert.Equal("Local host", approval.Presentation.Host);
        Assert.Null(approval.Presentation.WorkingDirectory);
        Assert.Equal(
            [
                ("panel_id", ProcessRuntimeContextProxy.ProcessPanelId.Value),
                ("sort", "pid_asc"),
                ("limit", "64"),
            ],
            approval.Presentation.Arguments.Select(argument =>
                (argument.Name, argument.DisplayValue)));
        Assert.Empty(fixture.Processes.Actions);

        var decision = await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(decision.IsAccepted);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Processes.CallCount);
        Assert.Equal(
            ProcessMonitorSort.ProcessIdAscending,
            Assert.Single(fixture.Processes.Actions).Request.Sort);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Approved);
    }

    [Fact]
    public async Task AskModeDenialReturnsStructuredFailureWithoutCallingHost()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Ask));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("List local processes."),
            CancellationToken.None).AsTask();
        var approval = await WaitForApprovalAsync(fixture.Runtime);

        var decision = await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: false,
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(decision.IsAccepted);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Processes.CallCount);
        Assert.Empty(fixture.Processes.Actions);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(AgentToolResultStatus.Failed, toolResult.Status);
        Assert.Equal("approval_denied", toolResult.StableCode);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task OffModeNeverCallsHostAndReturnsCapabilityDenial()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Off));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("List local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Processes.CallCount);
        Assert.Empty(fixture.Processes.Actions);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(
            "policy_denied",
            toolResult.StableCode);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task BroadMixedScopeRequiresOnlyEligibleProcessPanelId()
    {
        await using var omitted = ProcessRuntimeFixture.Create(
            ProcessScope.MixedOpenTab,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));

        var omittedResult = await omitted.Runtime.SendAsync(
            omitted.Prompt("List this tab's processes."),
            CancellationToken.None);

        Assert.True(omittedResult.IsSuccess);
        Assert.Equal(0, omitted.Processes.CallCount);
        Assert.Equal(
            "invalid_tool_arguments",
            ToolResultFromLastRequest(omitted.Provider).StableCode);
        var schema = omitted.Provider.Requests.ToArray()[0].Tools
            .Single(tool => tool.Name == BuiltInAgentTools.ProcessesList)
            .InputSchema;
        Assert.Equal(
            [ProcessRuntimeContextProxy.ProcessPanelId.Value],
            schema.GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "panel_id",
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));

        await using var selected = ProcessRuntimeFixture.Create(
            ProcessScope.MixedOpenTab,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                $$"""
                {
                  "panel_id": "{{ProcessRuntimeContextProxy.ProcessPanelId.Value}}",
                  "sort": "name_asc",
                  "limit": 32
                }
                """),
            ProcessPolicy(AgentPermission.Auto));

        var selectedResult = await selected.Runtime.SendAsync(
            selected.Prompt("List this tab's processes."),
            CancellationToken.None);

        Assert.True(selectedResult.IsSuccess);
        Assert.Equal(1, selected.Processes.CallCount);
        Assert.Equal(
            ProcessMonitorSort.NameAscending,
            Assert.Single(selected.Processes.Actions).Request.Sort);
        using var toolJson = JsonDocument.Parse(
            ToolResultFromLastRequest(selected.Provider).Value.Content);
        Assert.Equal(
            ProcessRuntimeContextProxy.ProcessPanelId.Value,
            toolJson.RootElement.GetProperty("panel_id").GetString());
        Assert.Equal(
            2,
            selected.Runtime.Snapshot.ContextItems.Length);
        Assert.Contains(
            selected.Runtime.Snapshot.ContextItems,
            item => item.Kind == PanelKind.ProcessMonitor);
        Assert.Contains(
            selected.Runtime.Snapshot.ContextItems,
            item => item.Kind == PanelKind.Terminal);
    }

    [Theory]
    [InlineData(
        BuiltInAgentTools.ProcessesList,
        """{"limit":16,"limit":32}""",
        "invalid_tool_arguments",
        false)]
    [InlineData(
        BuiltInAgentTools.ProcessesList,
        """{"include_command_line":true}""",
        "invalid_tool_arguments",
        true)]
    [InlineData(
        "processes.provider_extension",
        "{}",
        "unknown_tool",
        false)]
    public async Task InvalidUnknownOrDuplicateCallsNeverAuthorize(
        string toolName,
        string arguments,
        string expectedCode,
        bool reachesGovernedToolResult)
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(toolName, arguments),
            ProcessPolicy(AgentPermission.Auto));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None);

        Assert.Equal(0, fixture.Processes.CallCount);
        Assert.Empty(fixture.Processes.Actions);
        if (reachesGovernedToolResult)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(
                expectedCode,
                ToolResultFromLastRequest(fixture.Provider).StableCode);
        }
        else
        {
            Assert.False(result.IsSuccess);
            Assert.Single(fixture.Provider.Requests);
        }

        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList);
    }

    [Fact]
    public async Task FreshCapabilityLossRejectsBeforeAuthorization()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Context.RemoveCapabilityAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Processes.CallCount);
        Assert.Equal(
            "tool_not_available",
            ToolResultFromLastRequest(fixture.Provider).StableCode);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList);
    }

    [Fact]
    public async Task PinnedSessionDriftRejectsBeforeAuthorization()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Context.ReplaceSessionAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Processes.CallCount);
        Assert.Equal(
            "target_changed",
            ToolResultFromLastRequest(fixture.Provider).StableCode);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList);
    }

    [Fact]
    public async Task ActiveProcessCaptureCanBeCancelledWithoutStoppingRun()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Processes.BlockAfterAuthorization = true;

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None).AsTask();
        await fixture.Processes.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() =>
            fixture.Runtime.Snapshot.State
            == GovernedAgentState.RunningTool);

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.WasRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Processes.CallCount);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal("caller_cancelled", toolResult.StableCode);
        Assert.Equal(AgentToolResultStatus.Failed, toolResult.Status);
        Assert.Null(fixture.Runtime.Snapshot.ActiveTool);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Cancelled);
    }

    [Fact]
    public async Task CancellingSendTokenWhileHostIsBlockedCancelsTheTurn()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Processes.BlockAfterAuthorization = true;
        using var cancellation = new CancellationTokenSource();

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            cancellation.Token).AsTask();
        await fixture.Processes.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Equal(1, fixture.Processes.CallCount);
        Assert.Single(fixture.Processes.Actions);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(
            GovernedAgentState.Cancelled,
            fixture.Runtime.Snapshot.State);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Cancelled
                && auditEvent.Details
                    is AuditDetails.AgentActionDetails
                {
                    ResultCode: "caller_cancelled",
                });
    }

    [Theory]
    [InlineData(
        HostErrorCode.DeadlineExceeded,
        "monitor_deadline",
        "deadline_exceeded")]
    [InlineData(
        HostErrorCode.EngineFailed,
        "processes_unavailable",
        "processes_unavailable")]
    [InlineData(
        HostErrorCode.EngineFailed,
        "monitor_capture_failed",
        "processes_capture_failed")]
    [InlineData(
        HostErrorCode.CapabilityNotSupported,
        "monitor_capability_missing",
        "processes_unavailable")]
    public async Task HostFailuresUseClosedCodesAndStillContinueProvider(
        HostErrorCode code,
        string hostStableCode,
        string expectedCode)
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Processes.Failure = new HostError(
            code,
            hostStableCode,
            "password=secret-canary",
            Retryable: true);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Processes.CallCount);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(expectedCode, toolResult.StableCode);
        Assert.DoesNotContain(
            "monitor_",
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "secret-canary",
            toolResult.Value.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingHostUsesClosedFailureWithoutLeakingException()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.ProcessesList,
                "{}"),
            ProcessPolicy(AgentPermission.Auto));
        fixture.Processes.RunException = new InvalidOperationException(
            "password=secret-canary");

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect local processes."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Processes.CallCount);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(
            "processes_capture_failed",
            toolResult.StableCode);
        Assert.DoesNotContain(
            "secret-canary",
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "secret-canary",
                StringComparison.Ordinal));
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent =>
                auditEvent.Action == BuiltInAgentTools.ProcessesList
                && auditEvent.Outcome == AuditOutcome.Failed
                && auditEvent.Details
                    is AuditDetails.AgentActionDetails
                {
                    ResultCode: "processes_capture_failed",
                });
    }

    [Fact]
    public async Task RuntimeRequiresProcessHostAndComposerAsOneBoundary()
    {
        var sessionHost = DispatchProxy.Create<
            ISessionHostClient,
            ProcessRuntimeContextProxy>();
        var context = (ProcessRuntimeContextProxy)(object)sessionHost;
        context.Initialize(ProcessScope.ExactPanel);
        var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new RecordingAuditStore(),
            TimeProvider.System);
        var terminal = new RejectingTerminalHost();
        var provider = ScriptedProvider.AnswerOnly();

        try
        {
            Assert.Throws<ArgumentException>(() =>
                new GovernedAgentRuntime(
                    sessionHost,
                    broker,
                    terminal,
                    agentBrowserHost: null,
                    agentFileHost: null,
                    new AgentTerminalActionComposer(),
                    browserComposer: null,
                    fileComposer: null,
                    BuiltInAgentTools.Catalog,
                    new FixedProviderResolver(provider),
                    new TestApprovalPrincipal(context.ApprovalClientId),
                    TimeProvider.System,
                    ProcessPolicy(AgentPermission.Auto),
                    agentProcessHost: null,
                    processComposer: new AgentProcessListActionComposer()));
        }
        finally
        {
            await broker.DisposeAsync();
        }
    }

    private static AgentPolicy ProcessPolicy(AgentPermission permission) =>
        AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.ProcessControl,
                permission),
        };

    private static AgentToolResult ToolResultFromLastRequest(
        ScriptedProvider provider)
    {
        var message = Assert.Single(
            provider.Requests.ToArray()[^1].Messages,
            candidate => candidate.Role == AgentMessageRole.Tool);
        return message.ToolResult
            ?? throw new Xunit.Sdk.XunitException(
                "The continuation did not contain a structured tool result.");
    }

    private static async ValueTask<GovernedAgentApproval>
        WaitForApprovalAsync(GovernedAgentRuntime runtime)
    {
        await WaitUntilAsync(() =>
            runtime.Snapshot.State == GovernedAgentState.AwaitingApproval);
        return runtime.Snapshot.PendingApproval
            ?? throw new Xunit.Sdk.XunitException(
                "The runtime entered approval without a request.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The governed process runtime state did not arrive.");
            }

            await Task.Delay(10);
        }
    }

    private static ProcessMonitorEntry Entry(
        int processId,
        string name,
        long? workingSetBytes = 1_024,
        bool isGhostShell = false) =>
        new(
            processId,
            name,
            CpuPercent: 1,
            WorkingSetBytes: workingSetBytes,
            TotalProcessorTime: TimeSpan.FromSeconds(1),
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            IsGhostShell: isGhostShell);

    public enum ProcessScope
    {
        ExactPanel,
        ExactTerminal,
        MixedOpenTab,
    }

    private sealed class ProcessRuntimeFixture : IAsyncDisposable
    {
        private ProcessRuntimeFixture(
            ISessionHostClient sessionHost,
            ProcessRuntimeContextProxy context,
            ScriptedProvider provider,
            AgentPolicy policy,
            McpRuntimeHost? mcpHost = null)
        {
            Context = context;
            Provider = provider;
            Audit = new RecordingAuditStore();
            Broker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                Audit,
                TimeProvider.System);
            mcpHost?.Initialize(Broker, context);
            var processComposer =
                new AgentProcessListActionComposer();
            Terminal = new RejectingTerminalHost();
            Processes = new ConsumingProcessHost(
                Broker,
                processComposer,
                context);
            Runtime = new GovernedAgentRuntime(
                sessionHost,
                Broker,
                Terminal,
                agentBrowserHost: null,
                agentFileHost: null,
                new AgentTerminalActionComposer(),
                browserComposer: null,
                fileComposer: null,
                BuiltInAgentTools.Catalog,
                new FixedProviderResolver(provider),
                new TestApprovalPrincipal(context.ApprovalClientId),
                TimeProvider.System,
                policy,
                agentProcessHost: Processes,
                processComposer: processComposer,
                agentMcpHost: mcpHost,
                mcpComposer: mcpHost is null
                    ? null
                    : new AgentMcpToolCallActionComposer());
        }

        public ProcessRuntimeContextProxy Context { get; }

        public ScriptedProvider Provider { get; }

        public RecordingAuditStore Audit { get; }

        public AgentCapabilityBroker Broker { get; }

        public RejectingTerminalHost Terminal { get; }

        public ConsumingProcessHost Processes { get; }

        public GovernedAgentRuntime Runtime { get; }

        public static ProcessRuntimeFixture Create(
            ProcessScope scope,
            ScriptedProvider provider,
            AgentPolicy policy,
            McpRuntimeHost? mcpHost = null)
        {
            var sessionHost = DispatchProxy.Create<
                ISessionHostClient,
                ProcessRuntimeContextProxy>();
            var context =
                (ProcessRuntimeContextProxy)(object)sessionHost;
            context.Initialize(scope);
            return new ProcessRuntimeFixture(
                sessionHost,
                context,
                provider,
                policy,
                mcpHost);
        }

        public GovernedAgentPrompt Prompt(string message) =>
            new(
                new AiProviderProfileId("process-provider"),
                message,
                Context.Target);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Broker.DisposeAsync();
        }
    }

    public class ProcessRuntimeContextProxy : DispatchProxy
    {
        public static readonly WindowInstanceId WindowId =
            new("process-window");
        public static readonly WorkspaceInstanceId WorkspaceId =
            new("process-workspace");
        public static readonly TabInstanceId TabId =
            new("process-tab");
        public static readonly PanelInstanceId ProcessPanelId =
            new("process-panel");
        public static readonly SessionId ProcessSessionId =
            new("process-session");
        public static readonly PanelInstanceId TerminalPanelId =
            new("terminal-panel");
        public static readonly SessionId TerminalSessionId =
            new("terminal-session");
        public static readonly PanelInstanceId StatisticsPanelId =
            new("statistics-panel");
        public static readonly SessionId StatisticsSessionId =
            new("statistics-session");

        private ProcessScope _scope;
        private int _inspectionCount;

        public ClientId ApprovalClientId { get; } =
            new("process-desktop-client");

        public AgentTarget Target { get; private set; } = null!;

        public int RemoveCapabilityAfterInspection { get; set; } =
            int.MaxValue;

        public int ReplaceSessionAfterInspection { get; set; } =
            int.MaxValue;

        public int InspectionCount =>
            Volatile.Read(ref _inspectionCount);

        public void Initialize(ProcessScope scope)
        {
            _scope = scope;
            Target = scope switch
            {
                ProcessScope.ExactPanel => ExactProcessTarget(),
                ProcessScope.ExactTerminal => ExactTerminalTarget(),
                ProcessScope.MixedOpenTab => new AgentTarget.OpenTab(
                    WindowId,
                    WorkspaceId,
                    TabId),
                _ => throw new ArgumentOutOfRangeException(nameof(scope)),
            };
        }

        public AgentContextSnapshot ExactContext(AgentTarget target)
        {
            if (target is not AgentTarget.Panel panel
                || panel != ExactProcessTarget())
            {
                throw new ArgumentException(
                    "The process host received an unexpected exact target.",
                    nameof(target));
            }

            return CreateExactContext(target);
        }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.InspectAgentContextAsync)
                    when args is
                    [
                        AgentContextRequest request,
                        OperationContext _,
                        CancellationToken cancellationToken,
                    ] => InspectAsync(request, cancellationToken),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private ValueTask<HostResult<AgentContextSnapshot>> InspectAsync(
            AgentContextRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _inspectionCount);
            if (request.Target != Target)
            {
                return ValueTask.FromResult(
                    HostResult<AgentContextSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.NotFound,
                            "The process target is unavailable."),
                        1));
            }

            var snapshot = _scope switch
            {
                ProcessScope.ExactPanel =>
                    CreateExactContext(request.Target),
                ProcessScope.ExactTerminal =>
                    CreateExactTerminalContext(request.Target),
                ProcessScope.MixedOpenTab =>
                    CreateMixedContext(request.Target),
                _ => throw new ArgumentOutOfRangeException(nameof(_scope)),
            };
            return ValueTask.FromResult(
                HostResult<AgentContextSnapshot>.Succeed(
                    snapshot,
                    snapshot.Revision));
        }

        private AgentContextSnapshot CreateExactContext(
            AgentTarget target)
        {
            var processSessionId = CurrentProcessSessionId();
            var graph = Graph(
                [
                    new PanelInstance(
                        ProcessPanelId,
                        PanelKind.ProcessMonitor,
                        "Local processes",
                        processSessionId),
                ],
                ProcessPanelId);
            return new AgentContextSnapshot(
                target,
                [
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId,
                        ProcessPanelId,
                        ProcessDescriptor(processSessionId)),
                ],
                DateTimeOffset.UtcNow);
        }

        private AgentContextSnapshot CreateMixedContext(
            AgentTarget target)
        {
            var processSessionId = CurrentProcessSessionId();
            PanelInstance[] panels =
            [
                new(
                    ProcessPanelId,
                    PanelKind.ProcessMonitor,
                    "Local processes",
                    processSessionId),
                new(
                    TerminalPanelId,
                    PanelKind.Terminal,
                    "Shell",
                    TerminalSessionId),
                new(
                    StatisticsPanelId,
                    PanelKind.Statistics,
                    "Statistics",
                    StatisticsSessionId),
            ];
            var graph = Graph(panels, ProcessPanelId);
            return new AgentContextSnapshot(
                target,
                [
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId,
                        ProcessPanelId,
                        ProcessDescriptor(processSessionId)),
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId,
                        TerminalPanelId,
                        Descriptor(
                            TerminalSessionId,
                            TerminalPanelId,
                            PanelKind.Terminal,
                            CapabilitySet.Empty)),
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId,
                        StatisticsPanelId,
                        Descriptor(
                            StatisticsSessionId,
                            StatisticsPanelId,
                            PanelKind.Statistics,
                            CapabilitySet.Empty)),
                ],
                DateTimeOffset.UtcNow);
        }

        private AgentContextSnapshot CreateExactTerminalContext(
            AgentTarget target)
        {
            var graph = Graph(
                [
                    new PanelInstance(
                        TerminalPanelId,
                        PanelKind.Terminal,
                        "Shell",
                        TerminalSessionId),
                ],
                TerminalPanelId);
            return new AgentContextSnapshot(
                target,
                [
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId,
                        TerminalPanelId,
                        Descriptor(
                            TerminalSessionId,
                            TerminalPanelId,
                            PanelKind.Terminal,
                            CapabilitySet.Empty)),
                ],
                DateTimeOffset.UtcNow);
        }

        private WorkspaceGraphSnapshot Graph(
            IReadOnlyList<PanelInstance> panels,
            PanelInstanceId activePanelId) =>
            new(
                WindowId,
                new WorkspaceInstance(
                    WorkspaceId,
                    "Operations",
                    [
                        new TabInstance(
                            TabId,
                            "Local host",
                            panels,
                            activePanelId),
                    ],
                    TabId),
                revision: 1,
                lastSequence: 1);

        private SessionDescriptor ProcessDescriptor(
            SessionId sessionId)
        {
            var hasCapability =
                InspectionCount <= RemoveCapabilityAfterInspection;
            return Descriptor(
                sessionId,
                ProcessPanelId,
                PanelKind.ProcessMonitor,
                hasCapability
                    ? new CapabilitySet(
                        [SessionCapabilities.ProcessesList])
                    : CapabilitySet.Empty,
                revision: hasCapability ? 1 : 2);
        }

        private static SessionDescriptor Descriptor(
            SessionId sessionId,
            PanelInstanceId panelId,
            PanelKind kind,
            CapabilitySet capabilities,
            long revision = 1) =>
            new(
                sessionId,
                kind,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    WorkspaceId,
                    TabId,
                    panelId),
                capabilities,
                Revision: revision,
                HasActiveWork: false,
                StatusDetail: "Ready");

        private SessionId CurrentProcessSessionId() =>
            InspectionCount <= ReplaceSessionAfterInspection
                ? ProcessSessionId
                : new SessionId("replacement-process-session");

        private static AgentTarget.Panel ExactProcessTarget() =>
            new(
                WindowId,
                WorkspaceId,
                TabId,
                ProcessPanelId);

        private static AgentTarget.Panel ExactTerminalTarget() =>
            new(
                WindowId,
                WorkspaceId,
                TabId,
                TerminalPanelId);
    }

    private sealed class ConsumingProcessHost(
        IAgentCapabilityBroker broker,
        AgentProcessListActionComposer composer,
        ProcessRuntimeContextProxy context)
        : IAgentProcessSessionHost
    {
        private int _callCount;

        public ConcurrentQueue<AgentProcessListAction> Actions { get; } =
            [];

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public bool BlockAfterAuthorization { get; set; }

        public HostError? Failure { get; set; }

        public Exception? RunException { get; set; }

        public ProcessMonitorSnapshot Snapshot { get; set; } =
            new(
                DateTimeOffset.UnixEpoch,
                [Entry(7, "worker")],
                EnumeratedProcessCount: 1,
                ObservedProcessCount: 1,
                IsTruncated: false);

        public async ValueTask<HostResult<AgentProcessListResult>>
            RunAgentProcessListAsync(
                AgentAuthorizationId authorizationId,
                AgentProcessListAction action,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var binding = composer.BindForExecution(
                action,
                context.ExactContext(action.Proposal.Target));
            var consumed = await broker.ConsumeAsync(
                authorizationId,
                binding,
                cancellationToken);
            if (consumed is AgentPermitResult.Denied denied)
            {
                return HostResult<AgentProcessListResult>.Fail(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        denied.Error.Code.ToString().ToLowerInvariant(),
                        "The process authorization was denied."),
                    1);
            }

            var permit = ((AgentPermitResult.Granted)consumed).Permit;
            Actions.Enqueue(action);
            Started.TrySetResult();
            if (RunException is { } exception)
            {
                _ = await broker.CompleteAsync(
                    permit,
                    new AgentActionCompletion(
                        AgentActionOutcome.Failed,
                        "processes_capture_failed",
                        DateTimeOffset.UtcNow),
                    CancellationToken.None);
                throw exception;
            }

            if (BlockAfterAuthorization)
            {
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    _ = await broker.CompleteAsync(
                        permit,
                        new AgentActionCompletion(
                            AgentActionOutcome.Cancelled,
                            "caller_cancelled",
                            DateTimeOffset.UtcNow),
                        CancellationToken.None);
                    return HostResult<AgentProcessListResult>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "caller_cancelled",
                            "The process observation was cancelled."),
                        1);
                }
            }

            if (Failure is { } failure)
            {
                _ = await broker.CompleteAsync(
                    permit,
                    new AgentActionCompletion(
                        AgentActionOutcome.Failed,
                        "processes_capture_failed",
                        DateTimeOffset.UtcNow),
                    CancellationToken.None);
                return HostResult<AgentProcessListResult>.Fail(
                    failure,
                    1);
            }

            var result = composer.Project(action, Snapshot);
            var completion = await broker.CompleteAsync(
                permit,
                new AgentActionCompletion(
                    AgentActionOutcome.Succeeded,
                    "processes_listed",
                    DateTimeOffset.UtcNow,
                    result.ReturnedCount),
                CancellationToken.None);
            if (completion is not null)
            {
                return HostResult<AgentProcessListResult>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        AgentActionFailureCodes.CompletionAuditUnavailable,
                        "The process completion audit is unresolved."),
                    1);
            }

            return HostResult<AgentProcessListResult>.Succeed(
                result,
                1);
        }
    }

    private sealed class RejectingTerminalHost :
        IAgentTerminalSessionHost
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<HostResult<AgentTerminalActionResult>>
            RunAgentTerminalActionAsync(
                AgentAuthorizationId authorizationId,
                AgentTerminalAction action,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new Xunit.Sdk.XunitException(
                "A process runtime test dispatched a terminal action.");
        }
    }

    private sealed class FixedProviderResolver(IAgentProvider provider)
        : IAgentProviderResolver
    {
        private readonly FixedProviderBinding _binding = new(provider);

        public IAgentProviderBinding PinProvider(
            AiProviderProfileId profileId)
        {
            Assert.Equal(
                new AiProviderProfileId("process-provider"),
                profileId);
            return _binding;
        }
    }

    private sealed class FixedProviderBinding(IAgentProvider provider)
        : IAgentProviderBinding
    {
        public AiProviderProfileId ProfileId =>
            new("process-provider");

        public long Revision => 1;

        public string DefaultModel => "process-default-model";

        public bool IsCurrent => true;

        public IAgentProvider CreateProvider(string model) => provider;
    }

    private sealed class TestApprovalPrincipal(ClientId clientId)
        : IAgentApprovalPrincipal
    {
        public ActorDescriptor Actor { get; } =
            new(
                new ActorId(clientId.Value),
                ActorKind.Human,
                "Test process user",
                clientId);
    }

    private sealed class ScriptedProvider(
        Func<int, AgentProviderRequest, AgentProviderEvent[]> round)
        : IAgentProvider
    {
        private int _callCount;

        public ConcurrentQueue<AgentProviderRequest> Requests { get; } =
            [];

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _callCount);
            foreach (var providerEvent in round(call, request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return providerEvent;
                await Task.Yield();
            }
        }

        public static ScriptedProvider ToolThenAnswer(
            string toolName,
            string arguments) =>
            new((call, request) => call switch
            {
                1 => ToolCall(toolName, arguments),
                2 when request.Messages.Any(
                    message => message.Role == AgentMessageRole.Tool) =>
                    Answer("The process request was handled."),
                _ => throw new InvalidOperationException(
                    "The process provider received an unexpected round."),
            });

        public static ScriptedProvider AnswerOnly() =>
            new((call, _) => call == 1
                ? Answer("No process action was requested.")
                : throw new InvalidOperationException(
                    "The process provider received an unexpected round."));

        public static ScriptedProvider AnswersOnly() =>
            new((_, _) => Answer("No process action was requested."));

        private static AgentProviderEvent[] ToolCall(
            string toolName,
            string arguments) =>
        [
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.ToolCallStarted(
                0,
                "process-tool-call",
                ProviderToolName.FromInternal(toolName)),
            new AgentProviderEvent.ToolCallArgumentsDelta(
                0,
                arguments),
            new AgentProviderEvent.ToolCallCompleted(0),
            new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse),
        ];

        private static AgentProviderEvent[] Answer(string text) =>
        [
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta(text),
            new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn),
        ];
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        private readonly ConcurrentQueue<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events =>
            _events.ToArray();

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(auditEvent);
            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> values = Events
                .Where(item => item.CorrelationId == correlationId)
                .ToArray();
            return ValueTask.FromResult(
                AuditStoreResult<
                    IReadOnlyList<AuditEventRecord>>.Success(values));
        }
    }
}
