using GhostShell.Agent;
using GhostShell.Agent.Providers;
using GhostShell.Agent.Runtime;
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

        return new Binding(_providers.PinProvider(profileId));
    }

    private sealed class Binding(CatalogAiProviderBinding value)
        : IAgentProviderBinding
    {
        private readonly CatalogAiProviderBinding _value =
            value ?? throw new ArgumentNullException(nameof(value));

        public AiProviderProfileId ProfileId => _value.ProfileId;

        public long Revision => _value.Revision;

        public string DefaultModel => _value.DefaultModel;

        public bool IsCurrent => _value.IsCurrent;

        public IAgentProvider CreateProvider(string model) =>
            _value.CreateProvider(model);
    }
}
