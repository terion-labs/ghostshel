using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Immutable effective policy captured with a live runtime graph. Definition
/// changes can create new provenance values but cannot rewrite an accepted tab.
/// </summary>
public sealed record RuntimeAgentPolicyProvenance
{
    public RuntimeAgentPolicyProvenance(
        AgentPolicy effectivePolicy,
        IEnumerable<Source>? sources = null,
        bool isLegacyFallback = false,
        bool hasPolicyOverride = false)
    {
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        if (!effectivePolicy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "Runtime policy provenance requires a durable baseline policy.",
                nameof(effectivePolicy));
        }

        var copiedSources = sources?.ToArray() ?? [];
        if (copiedSources.Any(source => source is null)
            || copiedSources.Length > 2
            || copiedSources.Distinct().Count() != copiedSources.Length
            || copiedSources
                .GroupBy(source => source.Definition.Kind)
                .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Runtime policy provenance can contain one workspace and one screen source.",
                nameof(sources));
        }

        var normalizedPolicy = AgentPolicyResolver.Resolve(effectivePolicy);
        if (isLegacyFallback
            && (copiedSources.Length != 0
                || hasPolicyOverride
                || !PoliciesEqual(
                    normalizedPolicy,
                    AgentPolicyResolver.Resolve(AgentPolicy.Default))))
        {
            throw new ArgumentException(
                "Legacy recovery fallback must be the source-free default policy.",
                nameof(isLegacyFallback));
        }

        if (hasPolicyOverride && copiedSources.Length == 0)
        {
            throw new ArgumentException(
                "A durable runtime policy override requires definition provenance.",
                nameof(hasPolicyOverride));
        }

        EffectivePolicy = normalizedPolicy;
        Sources = Array.AsReadOnly(copiedSources);
        IsLegacyFallback = isLegacyFallback;
        HasPolicyOverride = hasPolicyOverride;
    }

    public static RuntimeAgentPolicyProvenance Default { get; } =
        new(AgentPolicy.Default);

    /// <summary>
    /// Marks schema-one/two recovery where no policy provenance existed. The
    /// default is captured directly; current definitions are never consulted.
    /// </summary>
    public static RuntimeAgentPolicyProvenance LegacyFallback { get; } =
        new(AgentPolicy.Default, isLegacyFallback: true);

    public AgentPolicy EffectivePolicy { get; }

    public IReadOnlyList<Source> Sources { get; }

    public bool IsLegacyFallback { get; }

    /// <summary>
    /// True only when at least one captured definition supplied an explicit
    /// durable policy. Definition lineage alone does not make the default
    /// provider/model an endpoint selection.
    /// </summary>
    public bool HasPolicyOverride { get; }

    public RuntimeAgentPolicyProvenance WithOverride(
        AgentPolicy? policy,
        DefinitionKey source,
        long revision)
    {
        return new(
            policy is null
                ? EffectivePolicy
                : AgentPolicyResolver.Resolve(EffectivePolicy, screen: policy),
            Sources.Append(new Source(source, revision)),
            isLegacyFallback: false,
            hasPolicyOverride: HasPolicyOverride || policy is not null);
    }

    private static bool PoliciesEqual(AgentPolicy left, AgentPolicy right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
        && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && left.CompactionModel == right.CompactionModel
        && left.TitleModel == right.TitleModel
        && string.Equals(left.SystemPrompt, right.SystemPrompt, StringComparison.Ordinal)
        && AgentPolicy.Capabilities.All(capability =>
            left.GetPermission(capability) == right.GetPermission(capability));

    public sealed record Source
    {
        public Source(DefinitionKey definition, long revision)
        {
            if (definition.Kind != ScreenDefinition.Kind
                && definition.Kind != WorkspaceDefinition.Kind)
            {
                throw new ArgumentException(
                    "Runtime policy sources must be saved screens or workspaces.",
                    nameof(definition));
            }

            if (revision <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision));
            }

            Definition = definition;
            Revision = revision;
        }

        public DefinitionKey Definition { get; }

        public long Revision { get; }
    }
}
