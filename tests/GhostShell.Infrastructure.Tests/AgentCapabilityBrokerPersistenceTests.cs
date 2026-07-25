using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class AgentCapabilityBrokerPersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviouslyClaimedActionCannotMintAuthorityAfterBrokerRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var proposal = Proposal();
        await using (var first = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new SqliteAuditStore(temporary.Database),
            new FixedTimeProvider(Now)))
        {
            Assert.Null(await first.RegisterRunAsync(
                Registration(),
                CancellationToken.None));
            Assert.IsType<AgentAuthorizationResult.Authorized>(
                await first.RequestAsync(proposal, CancellationToken.None));
        }

        await using var second = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new SqliteAuditStore(temporary.Database),
            new FixedTimeProvider(Now));
        Assert.Null(await second.RegisterRunAsync(
            Registration(),
            CancellationToken.None));

        var replay = await second.RequestAsync(proposal, CancellationToken.None);

        Assert.Equal(
            AgentAuthorizationErrorCode.DuplicateAction,
            Assert.IsType<AgentAuthorizationResult.Denied>(replay).Error.Code);
        var trail = await new SqliteAuditStore(temporary.Database)
            .ListByCorrelationAsync(proposal.Id.Value, CancellationToken.None);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved],
            trail.Value!.Select(item => item.Outcome));
    }

    private static AgentRunRegistration Registration() =>
        new(
            new AgentRunId("run-1"),
            Agent(),
            new ClientId("client-1"),
            Target(),
            AgentPolicy.Default,
            policyGeneration: 1);

    private static AgentActionProposal Proposal() =>
        new(
            new AgentActionId("action-1"),
            new AgentRunId("run-1"),
            Agent(),
            BuiltInAgentTools.TerminalReadScreen,
            Target(),
            AgentActionDigest.FromUtf8("workspace-revision-7"),
            AgentActionDigest.FromUtf8("read-screen"),
            new AgentApprovalPresentation(
                "Production shell",
                "server.example",
                "/srv/app"),
            policyGeneration: 1,
            Now,
            Now.AddMinutes(5));

    private static AgentTarget.Workspace Target() =>
        new(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"));

    private static ActorDescriptor Agent() =>
        new(new ActorId("agent-1"), ActorKind.Agent, "GhostSHELL agent");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
