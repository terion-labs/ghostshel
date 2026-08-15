using System.Text.Json.Serialization;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Exact legacy payload used only at the portable-definition trust boundary.
/// Runtime code receives a normalized schema-v2 <see cref="AiProviderProfile"/>.
/// </summary>
internal sealed record AiProviderProfileSchemaOne
{
    [JsonConstructor]
    public AiProviderProfileSchemaOne(
        AiProviderProfileId id,
        int schemaVersion,
        string name,
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel,
        int order,
        bool isEnabled)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        ProviderKind = providerKind;
        Endpoint = endpoint;
        Authentication = authentication;
        DefaultModel = defaultModel;
        Order = order;
        IsEnabled = isEnabled;
    }

    public AiProviderProfileId Id { get; }

    public int SchemaVersion { get; }

    public string Name { get; }

    public AiProviderKind ProviderKind { get; }

    public Uri Endpoint { get; }

    public AiProviderAuthentication Authentication { get; }

    public string DefaultModel { get; }

    public int Order { get; }

    public bool IsEnabled { get; }
}
