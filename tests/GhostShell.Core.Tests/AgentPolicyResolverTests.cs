using System.Collections.Immutable;

namespace GhostShell.Core.Tests;

public sealed class AgentPolicyResolverTests
{
    [Fact]
    public void DefaultPolicyDefinesEveryExecutionCapability()
    {
        Assert.True(AgentPolicy.Default.IsStructurallyValid());
        Assert.True(AgentPolicy.Default.IsValidForDurableStorage());
        Assert.Equal(
            AgentPolicy.Capabilities.Order(),
            AgentPolicy.Default.Permissions.Keys.Order());
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.SecretUse));
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.BrowserInteraction));
    }

    [Theory]
    [InlineData(AgentPermission.Off)]
    [InlineData(AgentPermission.Ask)]
    [InlineData(AgentPermission.Auto)]
    public void DurablePoliciesAcceptOrdinaryPermissionModes(AgentPermission permission)
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                permission),
        };

        Assert.True(policy.IsValidForDurableStorage());
    }

    [Fact]
    public void DurablePoliciesRejectYoloForEveryCapability()
    {
        foreach (var capability in AgentPolicy.Capabilities)
        {
            var policy = AgentPolicy.Default with
            {
                Permissions = AgentPolicy.Default.Permissions.SetItem(
                    capability,
                    AgentPermission.Yolo),
            };

            Assert.False(
                policy.IsValidForDurableStorage(),
                $"Capability '{capability}' accepted a durable YOLO permission.");
        }
    }

    [Fact]
    public void PolicyProviderAndModelIdentifiersAreBoundedAndControlFree()
    {
        var valid = AgentPolicy.Default with
        {
            Provider = new string('p', AgentPolicy.MaximumProviderLength),
            Model = new string('m', AgentPolicy.MaximumModelLength),
        };
        var invalid = new[]
        {
            valid with { Provider = string.Empty },
            valid with
            {
                Provider = new string('p', AgentPolicy.MaximumProviderLength + 1),
            },
            valid with { Provider = "provider\ninjected" },
            valid with { Model = " " },
            valid with
            {
                Model = new string('m', AgentPolicy.MaximumModelLength + 1),
            },
            valid with { Model = "model\0injected" },
        };

        Assert.True(valid.IsStructurallyValid());
        Assert.True(valid.IsValidForDurableStorage());
        Assert.All(
            invalid,
            policy =>
            {
                Assert.False(policy.IsStructurallyValid());
                Assert.False(policy.IsValidForDurableStorage());
            });
    }

    [Fact]
    public void PriorFullAndLegacyPoliciesRemainReadableAndNewCapabilitiesFailClosed()
    {
        var previousFull = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.Remove(
                AgentCapability.BrowserInteraction),
        };
        var legacy = LegacyPolicy(AgentPermission.Auto);
        var resolved = AgentPolicyResolver.Resolve(previousFull);

        Assert.True(previousFull.IsStructurallyValid());
        Assert.True(previousFull.IsValidForDurableStorage());
        Assert.Equal(
            AgentPermission.Off,
            previousFull.GetPermission(AgentCapability.BrowserInteraction));
        Assert.True(resolved.IsStructurallyValid());
        Assert.Equal(
            AgentPermission.Off,
            resolved.GetPermission(AgentCapability.BrowserInteraction));
        Assert.True(legacy.IsStructurallyValid());
        Assert.True(legacy.IsValidForDurableStorage());
        Assert.Equal(
            AgentPermission.Off,
            legacy.GetPermission(AgentCapability.McpTools));
    }

    [Fact]
    public void MostSpecificExplicitCapabilityWinsWhileMissingLegacyValuesFallBack()
    {
        var global = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Ask),
        };
        var workspace = LegacyPolicy(AgentPermission.Auto);

        var resolved = AgentPolicyResolver.Resolve(global, workspace);

        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Ask,
            resolved.GetPermission(AgentCapability.McpTools));
        Assert.Equal(workspace.Provider, resolved.Provider);
        Assert.Equal(workspace.Model, resolved.Model);
    }

    [Fact]
    public void LeastPrivilegeAggregationUsesTheMostRestrictivePermissionPerCapability()
    {
        var allAuto = FullPolicy("provider", "model", AgentPermission.Auto);
        var first = allAuto with
        {
            Permissions = allAuto.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Off)
                .SetItem(AgentCapability.EditFiles, AgentPermission.Auto)
                .SetItem(AgentCapability.Search, AgentPermission.Ask),
        };
        var second = allAuto with
        {
            Permissions = allAuto.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Ask)
                .SetItem(AgentCapability.EditFiles, AgentPermission.Ask)
                .SetItem(AgentCapability.Search, AgentPermission.Auto),
        };

        var resolved = AgentPolicyResolver.ResolveLeastPrivilege([first, second]);

        Assert.Equal("provider", resolved.Provider);
        Assert.Equal("model", resolved.Model);
        Assert.Equal(
            AgentPermission.Off,
            resolved.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Ask,
            resolved.GetPermission(AgentCapability.EditFiles));
        Assert.Equal(
            AgentPermission.Ask,
            resolved.GetPermission(AgentCapability.Search));
        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.TerminalRead));
    }

    [Theory]
    [InlineData("other-provider", "model")]
    [InlineData("provider", "other-model")]
    public void LeastPrivilegeAggregationRejectsProviderOrModelMismatch(
        string provider,
        string model)
    {
        var first = FullPolicy("provider", "model", AgentPermission.Ask);
        var second = FullPolicy(provider, model, AgentPermission.Ask);

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([first, second]));
    }

    [Fact]
    public void LeastPrivilegeAggregationNormalizesLegacyPoliciesAndFailsClosedForNewCapabilities()
    {
        var legacy = LegacyPolicy(AgentPermission.Auto);
        var current = FullPolicy(
            legacy.Provider,
            legacy.Model,
            AgentPermission.Auto);

        var resolved = AgentPolicyResolver.ResolveLeastPrivilege([legacy, current]);

        Assert.Equal(AgentPolicy.Capabilities.Order(), resolved.Permissions.Keys.Order());
        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Off,
            resolved.GetPermission(AgentCapability.TerminalRead));
        Assert.Equal(
            AgentPermission.Off,
            resolved.GetPermission(AgentCapability.BrowserInteraction));
    }

    [Fact]
    public void LeastPrivilegeAggregationRejectsEmptyScope()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([]));
    }

    [Fact]
    public void LeastPrivilegeAggregationRejectsNonDurablePolicy()
    {
        var yolo = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([yolo]));
    }

    [Theory]
    [InlineData(AgentPermission.Off, AgentActionRisk.Observation, AgentPolicyDecision.Denied)]
    [InlineData(AgentPermission.Ask, AgentActionRisk.Observation, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Observation, AgentPolicyDecision.AuthorizedByAuto)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Routine, AgentPolicyDecision.AuthorizedByAuto)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Mutation, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Destructive, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Privileged, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Yolo, AgentActionRisk.Privileged, AgentPolicyDecision.AuthorizedByYolo)]
    public void RiskClassificationCannotBeSuppliedByTheModelToBypassPolicy(
        AgentPermission permission,
        AgentActionRisk risk,
        AgentPolicyDecision expected)
    {
        Assert.Equal(expected, AgentPolicyResolver.Evaluate(permission, risk));
    }

    [Fact]
    public void MalformedLayerIsRejectedInsteadOfPartiallyApplied()
    {
        var malformed = AgentPolicy.Default with
        {
            Permissions = ImmutableDictionary<AgentCapability, AgentPermission>.Empty,
        };

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.Resolve(AgentPolicy.Default, malformed));
    }

    private static AgentPolicy FullPolicy(
        string provider,
        string model,
        AgentPermission permission) => new(
        provider,
        model,
        AgentPolicy.Capabilities.ToImmutableDictionary(
            capability => capability,
            _ => permission));

    private static AgentPolicy LegacyPolicy(AgentPermission commands) => new(
        "legacy-provider",
        "legacy-model",
        new Dictionary<AgentCapability, AgentPermission>
        {
            [AgentCapability.RunCommands] = commands,
            [AgentCapability.EditFiles] = AgentPermission.Ask,
            [AgentCapability.ReadFiles] = AgentPermission.Auto,
            [AgentCapability.Search] = AgentPermission.Auto,
            [AgentCapability.Git] = AgentPermission.Ask,
            [AgentCapability.WebFetch] = AgentPermission.Ask,
            [AgentCapability.Docker] = AgentPermission.Off,
        }.ToImmutableDictionary());
}
