using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task QueuedFollowUpRunsAfterCurrentTurnWithItsReasoningEffort()
    {
        var provider = new ProviderRound((_, _) => Answer("Completed."))
        {
            BlockOnCall = 1,
        };
        await using var fixture = new RuntimeFixture(provider);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("First question."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queued = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Second question.",
                AgentReasoningEffort.High),
            CancellationToken.None);

        Assert.True(queued.IsAccepted);
        Assert.Equal(1, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        provider.ReleaseBlockedCall.TrySetResult();

        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Equal(2, provider.Requests.Count);
        var requests = provider.Requests.ToArray();
        Assert.Equal(AgentReasoningEffort.Automatic, requests[0].ReasoningEffort);
        Assert.Equal(AgentReasoningEffort.High, requests[1].ReasoningEffort);
        Assert.Contains(
            requests[1].Messages,
            message => message.Role == AgentMessageRole.User
                && message.Content == "Second question.");
        Assert.Equal(
            new[]
            {
                "First question.",
                "Completed.",
                "Second question.",
                "Completed.",
            },
            fixture.Runtime.Snapshot.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task FailedQueuedFollowUpIsReturnedForDraftRecovery()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Answer("Initial answer."),
            2 => [new AgentProviderEvent.ResponseStarted()],
            _ => throw new InvalidOperationException(
                "The provider received an unexpected request."),
        })
        {
            BlockOnCall = 1,
        };
        await using var fixture = new RuntimeFixture(provider);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Initial question."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Preserve this follow-up.",
                AgentReasoningEffort.High),
            CancellationToken.None);
        provider.ReleaseBlockedCall.TrySetResult();

        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(queued.IsAccepted);
        Assert.False(result.IsSuccess);
        Assert.True(result.InitialPromptCommitted);
        var recoverable = Assert.Single(result.RecoverableFollowUps!);
        Assert.Equal("Preserve this follow-up.", recoverable.Message);
        Assert.Equal(AgentReasoningEffort.High, recoverable.ReasoningEffort);
        Assert.Equal(GovernedAgentState.Failed, fixture.Runtime.Snapshot.State);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task FollowUpQueueIsBoundedAndStopDiscardsIt()
    {
        var provider = ProviderRound.Blocking();
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Wait."),
            CancellationToken.None).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var index = 0; index < 8; index++)
        {
            var queued = await fixture.Runtime.QueueFollowUpAsync(
                new GovernedAgentFollowUp($"Follow-up {index}."),
                CancellationToken.None);
            Assert.True(queued.IsAccepted);
        }

        var overflow = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("One too many."),
            CancellationToken.None);
        Assert.False(overflow.IsAccepted);
        Assert.Equal("agent_follow_up_queue_full", overflow.Code);
        Assert.Equal(8, fixture.Runtime.Snapshot.QueuedFollowUpCount);

        Assert.True((await fixture.Runtime.StopAsync(CancellationToken.None)).WasRunning);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Single(provider.Requests);
    }
}
