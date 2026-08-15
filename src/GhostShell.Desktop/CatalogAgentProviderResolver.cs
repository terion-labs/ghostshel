using GhostShell.Agent;
using GhostShell.Agent.Providers;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class CatalogAgentProviderResolver(
    CatalogAiProviderRuntime providers)
    : IAgentProviderResolver
{
    private readonly CatalogAiProviderRuntime _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));

    public IAgentProviderBinding PinProvider(AiProviderProfileId profileId)
    {
        var profile = _providers.Profiles.SingleOrDefault(
            candidate => candidate.Id == profileId);
        if (profile is null || !profile.IsEnabled)
        {
            throw new KeyNotFoundException(
                "The requested enabled AI-provider profile is unavailable.");
        }

        return new Binding(_providers.PinProvider(profileId), profile);
    }

    private sealed class Binding(
        CatalogAiProviderBinding value,
        AiProviderProfileDescriptor profile)
        : IAgentProviderBinding
    {
        private readonly CatalogAiProviderBinding _value =
            value ?? throw new ArgumentNullException(nameof(value));
        private readonly AiProviderProfileDescriptor _profile =
            profile ?? throw new ArgumentNullException(nameof(profile));

        public AiProviderProfileId ProfileId => _value.ProfileId;

        public long Revision => _value.Revision;

        public string DefaultModel => _value.DefaultModel;

        public bool IsCurrent => _value.IsCurrent;

        public int? ContextWindowTokens(string model) => _profile.Models
            .SingleOrDefault(candidate => string.Equals(
                candidate.Id,
                model,
                StringComparison.Ordinal))
            ?.ContextWindowTokens;

        public IAgentProvider CreateProvider(string model) =>
            _value.CreateProvider(model);

        public IAgentProvider CreateProvider(
            string model,
            AgentServiceTier serviceTier) =>
            _value.CreateProvider(model, serviceTier);
    }
}
