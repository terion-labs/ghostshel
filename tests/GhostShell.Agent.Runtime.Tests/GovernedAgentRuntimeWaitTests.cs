using GhostShell.Agent;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task WaitForChangeProposalUsesTheClosedTypedHostRequest()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer(
                """{"after_content_revision":7,"timeout_ms":1000}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Changed(
                    fixture.Context.Screen("changed", contentRevision: 8),
                    initialContentRevision: 7)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Wait for the terminal to change."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.IsType<AgentTerminalRequest.WaitForChange>(
            Assert.Single(fixture.Terminal.Actions).Request);
        Assert.Equal(fixture.Context.SessionId, request.Value.SessionId);
        Assert.Equal(7, request.Value.Wait.AfterContentRevision);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Value.Wait.Timeout);
        Assert.Contains(
            "\"wait_outcome\":\"changed\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"panel_id\":\"panel-1\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForStableProposalUsesTheClosedTypedHostRequest()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer(
                """{"stable_for_ms":250,"timeout_ms":1000}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Stable(
                    fixture.Context.Screen("stable", contentRevision: 8),
                    initialContentRevision: 8)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Wait until the terminal settles."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.IsType<AgentTerminalRequest.WaitForStable>(
            Assert.Single(fixture.Terminal.Actions).Request);
        Assert.Equal(fixture.Context.SessionId, request.Value.SessionId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), request.Value.Wait.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Value.Wait.Timeout);
        Assert.Contains(
            "\"wait_outcome\":\"stable\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    private static ProviderRound WaitThenAnswer(string arguments) =>
        new((call, request) => call switch
        {
            1 =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ToolCallStarted(
                    0,
                    "provider-wait",
                    ProviderToolName.FromInternal(BuiltInAgentTools.TerminalWait)),
                new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments),
                new AgentProviderEvent.ToolCallCompleted(0),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse),
            ],
            2 when request.Messages.Any(
                message => message.Role == AgentMessageRole.Tool) =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.TextDelta("The terminal wait completed."),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn),
            ],
            _ => throw new InvalidOperationException(
                "The wait provider received an unexpected round."),
        });

    private static string ToolResult(RuntimeFixture fixture)
    {
        var continuation = fixture.Provider.Requests.ToArray()[1];
        return Assert.Single(
                continuation.Messages,
                message => message.Role == AgentMessageRole.Tool)
            .ToolResult!
            .Value
            .Content;
    }
}
