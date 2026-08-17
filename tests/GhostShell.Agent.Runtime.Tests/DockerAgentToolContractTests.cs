using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;

namespace GhostShell.Agent.Runtime.Tests;

public sealed class DockerAgentToolContractTests
{
    [Fact]
    public void SchemasAreClosedAndExposeOnlyLiveCapabilities()
    {
        var panel = ContextPanel("docker",
            SessionCapabilities.DockerReadState,
            SessionCapabilities.DockerInspect,
            SessionCapabilities.DockerReadLogs,
            SessionCapabilities.DockerFilesRead);

        var tools = DockerAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.DockerReadState,
                BuiltInAgentTools.DockerInspect,
                BuiltInAgentTools.DockerLogs,
                BuiltInAgentTools.DockerFileRead,
            ],
            tools.Select(tool => tool.Name));
        Assert.All(tools, tool =>
        {
            Assert.False(tool.InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
            Assert.DoesNotContain("panel_id", tool.InputSchema.GetRawText());
            Assert.DoesNotContain("endpoint", tool.InputSchema.GetRawText());
            Assert.DoesNotContain("resource_id", tool.InputSchema.GetRawText());
        });

        var broad = Assert.Single(DockerAgentToolSet.For([
            ContextPanel("docker-a", SessionCapabilities.DockerInspect),
            ContextPanel("docker-b", SessionCapabilities.DockerReadState),
        ]), tool => tool.Name == BuiltInAgentTools.DockerInspect);
        Assert.Equal(
            ["panel-docker-a"],
            broad.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));

        var logs = Assert.Single(tools, tool =>
            tool.Name == BuiltInAgentTools.DockerLogs);
        Assert.True(logs.InputSchema
            .GetProperty("properties")
            .TryGetProperty("before_timestamp", out _));
        Assert.False(logs.InputSchema
            .GetProperty("properties")
            .TryGetProperty("since_timestamp", out _));

        var workspaceLogs = Assert.Single(
            DockerAgentToolSet.ForWorkspace(),
            tool => tool.Name == BuiltInAgentTools.DockerLogs);
        Assert.True(workspaceLogs.InputSchema
            .GetProperty("properties")
            .TryGetProperty("before_timestamp", out _));
        Assert.False(workspaceLogs.InputSchema
            .GetProperty("properties")
            .TryGetProperty("since_timestamp", out _));
    }

    [Fact]
    public async Task ParserBuildsTypedRequestsAndRejectsUnknownOrSecretArguments()
    {
        var logsPanel = ContextPanel("docker", SessionCapabilities.DockerReadLogs);
        var parsed = Assert.IsType<DockerAgentIntentResult.Parsed>(
            DockerAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DockerLogs,
                    """
                    {"container_ref":"opaque_ref","limit":25,"search":"error","context_lines":2}
                    """),
                logsPanel));
        var logs = Assert.IsType<AgentDockerReadRequest.Logs>(parsed.Request);
        Assert.Equal(25, logs.Limit);
        Assert.Equal("error", logs.SearchText);

        Assert.IsType<DockerAgentIntentResult.Rejected>(
            DockerAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DockerLogs,
                    """{"container_ref":"opaque_ref","unknown":true}"""),
                logsPanel));
        Assert.IsType<DockerAgentIntentResult.Rejected>(
            DockerAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DockerLogs,
                    """{"container_ref":"opaque_ref","search":"password=hunter2"}"""),
                logsPanel));
        Assert.IsType<DockerAgentIntentResult.Rejected>(
            DockerAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DockerLogs,
                    """{"container_ref":"opaque_ref","before_timestamp":"1","since_timestamp":"2"}"""),
                logsPanel));
    }

    [Fact]
    public void ResultJsonRedactsInspectLogsAndFileTextAndStaysBounded()
    {
        var composer = new AgentDockerReadActionComposer();
        var resource = new DockerResourceItem(
            new DockerResourceReferenceId("opaque_ref"),
            DockerResourceKind.Container,
            "api");
        var inspection = DockerAgentToolResultJson.Project(
            composer.Project(
                Prepare(
                    composer,
                    ContextPanel("docker", SessionCapabilities.DockerInspect),
                    new AgentDockerReadRequest.Inspect(
                        new PanelInstanceId("panel-docker"),
                        resource.Reference)),
                new DockerInspectionSnapshot(
                resource,
                [new DockerInspectionProperty(
                    "Config.Image",
                    "authorization: bearer abcdefghijklmnop")],
                IsTruncated: false)));
        var logs = DockerAgentToolResultJson.Project(
            composer.Project(
                Prepare(
                    composer,
                    ContextPanel("docker", SessionCapabilities.DockerReadLogs),
                    new AgentDockerReadRequest.Logs(
                        new PanelInstanceId("panel-docker"),
                        resource.Reference,
                        1,
                        null,
                        null,
                        null,
                        0)),
                new DockerContainerLogPage(
                [new DockerContainerLogLine("now", "token=abcdefghijklmnop")],
                HasOlder: false,
                OldestTimestamp: null,
                NewestTimestamp: null)));
        var file = DockerAgentToolResultJson.Project(
            composer.Project(
                Prepare(
                    composer,
                    ContextPanel("docker", SessionCapabilities.DockerFilesRead),
                    new AgentDockerReadRequest.FileRead(
                        new PanelInstanceId("panel-docker"),
                        resource.Reference,
                        "/srv/config.txt",
                        128)),
                new DockerFileSnapshot(
                    resource,
                    "/srv/config.txt",
                    "password=hunter2"u8.ToArray(),
                    IsTruncated: false)));

        foreach (var projection in new[] { inspection, logs, file })
        {
            Assert.True(projection.IsSuccess);
            Assert.DoesNotContain("hunter2", projection.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnop", projection.Json, StringComparison.Ordinal);
            Assert.True(Encoding.UTF8.GetByteCount(projection.Json)
                <= AgentKernelLimits.Default.MaximumToolResultBytes);
            using var document = JsonDocument.Parse(projection.Json);
            Assert.Equal(
                DockerAgentToolResultJson.ContentOrigin,
                document.RootElement.GetProperty("content_origin").GetString());
            Assert.True(document.RootElement.GetProperty("redaction_count").GetInt32() > 0);
        }
    }

    private static AgentDockerReadAction Prepare(
        AgentDockerReadActionComposer composer,
        AgentContextPanel panel,
        AgentDockerReadRequest request)
    {
        var now = DateTimeOffset.UnixEpoch;
        return composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("docker-result-run"),
                new ActorDescriptor(
                    new ActorId("docker-result-agent"),
                    ActorKind.Agent,
                    "Docker result agent"),
                policyGeneration: 1,
                now,
                now.AddMinutes(1)),
            new AgentContextSnapshot(
                new AgentTarget.Panel(
                    panel.WindowId,
                    panel.WorkspaceId,
                    panel.TabId,
                    panel.PanelId),
                [panel],
                now),
            request);
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("docker-contract"));
        var result = await session.RunTurnAsync(
            "Use the Docker tool.",
            [new AgentToolDefinition(
                name,
                "Test Docker tool.",
                """{"type":"object","additionalProperties":true}"""u8.ToArray())],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentContextPanel ContextPanel(
        string suffix,
        params string[] capabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId($"panel-{suffix}");
        var panel = new PanelInstance(
            panelId,
            PanelKind.Docker,
            "Docker",
            sessionId);
        var tab = new TabInstance(tabId, "Docker", [panel], panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(workspaceId, "Docker", [tab], tabId),
            revision: 2,
            lastSequence: 2);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Docker,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(capabilities),
            Revision: 4,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(graph, tabId, panelId, descriptor);
    }

    private sealed class ToolProvider(
        string name,
        string arguments) : IAgentProvider
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
                "docker-call",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
