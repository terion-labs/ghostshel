using System.Net;
using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Durable model-provider configuration. Credential values are represented only by an opaque
/// vault reference and are resolved by the provider adapter for the lifetime of one request.
/// </summary>
public sealed record AiProviderProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumNameLength = 128;
    public const int MaximumModelIdLength = 256;
    public const int MaximumEndpointLength = 2_048;
    public const int MaximumOrder = 10_000;

    [JsonConstructor]
    public AiProviderProfile(
        AiProviderProfileId id,
        int schemaVersion,
        string name,
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel,
        int order,
        bool isEnabled = true)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The AI-provider schema version is not supported.");
        }

        if (!Enum.IsDefined(providerKind))
        {
            throw new ArgumentOutOfRangeException(nameof(providerKind), providerKind, null);
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RequirePrintable(name, nameof(name), MaximumNameLength);
        ProviderKind = providerKind;
        Endpoint = NormalizeEndpoint(endpoint);
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        if (Authentication is AiProviderAuthentication.None && !IsLoopback(Endpoint))
        {
            throw new ArgumentException(
                "A provider without authentication must use an exact loopback endpoint.",
                nameof(authentication));
        }

        DefaultModel = RequirePrintable(
            defaultModel,
            nameof(defaultModel),
            MaximumModelIdLength);
        if (order is < 0 or > MaximumOrder)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order),
                order,
                $"Provider order must be between 0 and {MaximumOrder}.");
        }

        Order = order;
        IsEnabled = isEnabled;
    }

    public static DefinitionKind Kind => DefinitionKind.AiProviderProfile;

    public AiProviderProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public AiProviderKind ProviderKind { get; }

    public Uri Endpoint { get; }

    public AiProviderAuthentication Authentication { get; }

    public string DefaultModel { get; }

    public int Order { get; }

    public bool IsEnabled { get; }

    public static Uri DefaultEndpoint(AiProviderKind providerKind) => providerKind switch
    {
        AiProviderKind.Anthropic => new("https://api.anthropic.com/v1/"),
        AiProviderKind.OpenAi => new("https://api.openai.com/v1/"),
        AiProviderKind.OpenAiCompatible => new("http://localhost:11434/v1/"),
        _ => throw new ArgumentOutOfRangeException(nameof(providerKind), providerKind, null),
    };

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || endpoint.AbsoluteUri.Length > MaximumEndpointLength)
        {
            throw new ArgumentException(
                "The provider endpoint must be a bounded absolute HTTP(S) URI.",
                nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The provider endpoint cannot contain credentials, a query, or a fragment.",
                nameof(endpoint));
        }

        if (endpoint.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            throw new ArgumentException(
                "The provider endpoint must contain a valid DNS or IP host.",
                nameof(endpoint));
        }

        if (endpoint.Scheme == "http" && !IsLoopback(endpoint))
        {
            throw new ArgumentException(
                "Plain HTTP is allowed only for an exact loopback endpoint.",
                nameof(endpoint));
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? endpoint.AbsolutePath
                : $"{endpoint.AbsolutePath}/",
        };
        return builder.Uri;
    }

    private static bool IsLoopback(Uri endpoint)
    {
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(endpoint.Host, out var address)
            && IPAddress.IsLoopback(address);
    }

    private static string RequirePrintable(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The value must be a bounded printable string.",
                parameterName);
        }

        return normalized;
    }
}
