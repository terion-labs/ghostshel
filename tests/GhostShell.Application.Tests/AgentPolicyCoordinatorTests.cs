using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class AgentPolicyCoordinatorTests
{
    [Fact]
    public async Task SavedDefaultBecomesTheActiveGlobalPolicy()
    {
        var store = new MemoryStore();
        var coordinator = new AgentPolicyCoordinator(store);
        var changed = 0;
        coordinator.Changed += (_, _) => changed++;
        var policy = new AgentPolicy(
            "provider-openai",
            "gpt-5.6-terra",
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                _ => AgentPermission.Ask))
        {
            CompactionModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-terra"),
            TitleModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-terra"),
            SystemPrompt = "  Follow the repository conventions.  ",
        };

        var result = await coordinator.SaveAsync(policy, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(policy.Provider, store.Policy?.Provider);
        Assert.Equal(policy.Model, store.Policy?.Model);
        Assert.Equal(policy.Provider, coordinator.Policy?.Provider);
        Assert.Equal(policy.Model, coordinator.Policy?.Model);
        Assert.Equal(policy.CompactionModel, store.Policy?.CompactionModel);
        Assert.Equal(policy.TitleModel, store.Policy?.TitleModel);
        Assert.Equal(policy.CompactionModel, coordinator.Policy?.CompactionModel);
        Assert.Equal(policy.TitleModel, coordinator.Policy?.TitleModel);
        Assert.Equal("Follow the repository conventions.", store.Policy?.SystemPrompt);
        Assert.Equal("Follow the repository conventions.", coordinator.Policy?.SystemPrompt);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task InitializeLoadsTheDurableDefaultWithoutPublishingAChange()
    {
        var store = new MemoryStore { Policy = AgentPolicy.Default };
        var coordinator = new AgentPolicyCoordinator(store);
        var changed = 0;
        coordinator.Changed += (_, _) => changed++;

        await coordinator.InitializeAsync(CancellationToken.None);

        Assert.Equal(AgentPolicy.Default.Provider, coordinator.Policy?.Provider);
        Assert.Equal(0, changed);
    }

    private sealed class MemoryStore : IAgentPolicyPreferenceStore
    {
        public AgentPolicy? Policy { get; set; }

        public ValueTask<ApplicationRunResult<AgentPolicy?>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ApplicationRunResult<AgentPolicy?>.Success(Policy));
        }

        public ValueTask<ApplicationRunResult<Unit>> WriteAsync(
            AgentPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Policy = policy;
            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }
    }
}
