using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost.Tests;

public sealed class AttachmentAndLeaseTests
{
    [Fact]
    public async Task ReadAttachmentsCoexistAndVisualDetachKeepsSessionAlive()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var first = await harness.AttachAsync(AttachmentKind.ReadOnly, new ClientId("reader-1"));
        var second = await harness.AttachAsync(AttachmentKind.ReadOnly, new ClientId("reader-2"));

        Assert.NotEqual(first.Attachment.Id, second.Attachment.Id);
        Assert.Equal(2, second.Snapshot.Attachments.Count);

        var detached = await harness.Client.DetachAsync(
            new DetachSessionRequest(first.Attachment.Id, harness.SessionId),
            harness.HumanContext(new ClientId("reader-1")),
            CancellationToken.None);

        _ = detached.Value();
        var snapshot = (await harness.Client.GetSnapshotAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Single(snapshot.Attachments);
        Assert.NotEqual(SessionLifecycle.Closed, snapshot.Descriptor.Lifecycle);
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task HumanLeasePreemptsAgentAndAgentCannotPreemptHuman()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();

        var agentLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();
        Assert.True(agentLease.Granted);

        var humanLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.True(humanLease.Granted);
        Assert.True(humanLease.PreemptedAnotherHolder);

        var deniedAgent = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();
        Assert.False(deniedAgent.Granted);
        Assert.Equal(humanLease.Lease?.Id, deniedAgent.Lease?.Id);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeAcquiredByAnotherActor()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        _ = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(1)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var human = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(1)),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.True(human.Granted);
        Assert.False(human.PreemptedAnotherHolder);
    }
}
