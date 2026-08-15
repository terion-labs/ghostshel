using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class SqliteAgentPolicyPreferenceStoreTests
{
    [Fact]
    public async Task DefaultPolicySurvivesDatabaseReopen()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentPolicyPreferenceStore(temporary.Database);
        var policy = new AgentPolicy(
            "provider-openai",
            "gpt-5.6-terra",
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                _ => AgentPermission.Ask))
        {
            CompactionModel = null,
            TitleModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-sol"),
            SystemPrompt = "Follow this workspace's repository conventions.",
        };

        Assert.True((await store.WriteAsync(policy, CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteAgentPolicyPreferenceStore(temporary.Database);

        var result = await store.ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(policy.Provider, result.Value?.Provider);
        Assert.Equal(policy.Model, result.Value?.Model);
        Assert.Null(result.Value?.CompactionModel);
        Assert.Equal(policy.TitleModel, result.Value?.TitleModel);
        Assert.Equal(policy.SystemPrompt, result.Value?.SystemPrompt);
        Assert.Equal(
            policy.Permissions.OrderBy(pair => pair.Key),
            result.Value?.Permissions.OrderBy(pair => pair.Key));
    }
}
