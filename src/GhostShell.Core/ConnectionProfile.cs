using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Durable connection configuration. Runtime health and connection state belong to session projections,
/// and credential material is represented only by opaque <see cref="SecretRef"/> values.
/// </summary>
public sealed record ConnectionProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ConnectionProfile(
        ConnectionId id,
        int schemaVersion,
        string name,
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        ConnectionStartup startup,
        ConnectionKeepAlive keepAlive,
        SshHostKeyPolicy hostKeyPolicy,
        IReadOnlyList<string>? tags = null)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema versions start at one.");
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RuntimeId.Require(name, nameof(name)).Trim();
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        KeepAlive = keepAlive ?? throw new ArgumentNullException(nameof(keepAlive));
        HostKeyPolicy = hostKeyPolicy;
        Tags = NormalizeTags(tags);

        if (!Enum.IsDefined(hostKeyPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(hostKeyPolicy), hostKeyPolicy, "The host-key policy is not recognized.");
        }

        ValidateEndpointPolicy();
    }

    public static DefinitionKind Kind => DefinitionKind.Connection;

    public ConnectionId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    [JsonIgnore]
    public ConnectionKind ConnectionKind => Endpoint.Kind;

    public ConnectionEndpoint Endpoint { get; }

    public ConnectionAuthentication Authentication { get; }

    public ConnectionStartup Startup { get; }

    public ConnectionKeepAlive KeepAlive { get; }

    public SshHostKeyPolicy HostKeyPolicy { get; }

    public IReadOnlyList<string> Tags { get; }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var normalized = (tags ?? [])
            .Select(tag => RuntimeId.Require(tag, nameof(tags)).Trim())
            .ToArray();

        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw new ArgumentException("Connection tags must be unique.", nameof(tags));
        }

        return Array.AsReadOnly(normalized);
    }

    private void ValidateEndpointPolicy()
    {
        if (Endpoint.Kind == ConnectionKind.Ssh)
        {
            if (HostKeyPolicy == SshHostKeyPolicy.NotApplicable)
            {
                throw new ArgumentException("SSH connections require an explicit host-key policy.", nameof(HostKeyPolicy));
            }

            return;
        }

        if (Authentication is not ConnectionAuthentication.None)
        {
            throw new ArgumentException("Only SSH endpoints accept connection authentication.", nameof(Authentication));
        }

        if (HostKeyPolicy != SshHostKeyPolicy.NotApplicable)
        {
            throw new ArgumentException("Host-key policy only applies to SSH endpoints.", nameof(HostKeyPolicy));
        }
    }
}
