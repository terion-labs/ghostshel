using System.Text.Json.Serialization;

namespace GhostShell.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AiProviderAuthentication.None), "none")]
[JsonDerivedType(typeof(AiProviderAuthentication.ApiKey), "api-key")]
public abstract record AiProviderAuthentication
{
    private AiProviderAuthentication()
    {
    }

    public sealed record None : AiProviderAuthentication;

    public sealed record ApiKey : AiProviderAuthentication
    {
        [JsonConstructor]
        public ApiKey(SecretRef secret)
        {
            RuntimeId.Require(secret.Value, nameof(secret));
            Secret = secret;
        }

        public SecretRef Secret { get; }
    }
}
