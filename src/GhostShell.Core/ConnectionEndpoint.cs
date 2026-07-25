using System.Text.Json.Serialization;

namespace GhostShell.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ConnectionEndpoint.Local), "local")]
[JsonDerivedType(typeof(ConnectionEndpoint.Ssh), "ssh")]
[JsonDerivedType(typeof(ConnectionEndpoint.Docker), "docker")]
[JsonDerivedType(typeof(ConnectionEndpoint.Wsl), "wsl")]
public abstract record ConnectionEndpoint
{
    private ConnectionEndpoint()
    {
    }

    [JsonIgnore]
    public abstract ConnectionKind Kind { get; }

    public sealed record Local : ConnectionEndpoint
    {
        [JsonConstructor]
        public Local(string? shellPath = null)
        {
            ShellPath = NormalizeOptional(shellPath);
        }

        public string? ShellPath { get; }

        public override ConnectionKind Kind => ConnectionKind.Local;
    }

    public sealed record Ssh : ConnectionEndpoint
    {
        [JsonConstructor]
        public Ssh(string host, int port = 22, string? username = null)
        {
            Host = RuntimeId.Require(host, nameof(host));
            if (port is < 1 or > 65_535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "An SSH port must be between 1 and 65535.");
            }

            Port = port;
            Username = NormalizeOptional(username);
        }

        public string Host { get; }

        public int Port { get; }

        public string? Username { get; }

        public override ConnectionKind Kind => ConnectionKind.Ssh;
    }

    public sealed record Docker : ConnectionEndpoint
    {
        [JsonConstructor]
        public Docker(string container, string? context = null)
        {
            Container = RuntimeId.Require(container, nameof(container));
            Context = NormalizeOptional(context);
        }

        public string Container { get; }

        public string? Context { get; }

        public override ConnectionKind Kind => ConnectionKind.Docker;
    }

    public sealed record Wsl : ConnectionEndpoint
    {
        [JsonConstructor]
        public Wsl(string distribution, string? username = null)
        {
            Distribution = RuntimeId.Require(distribution, nameof(distribution));
            Username = NormalizeOptional(username);
        }

        public string Distribution { get; }

        public string? Username { get; }

        public override ConnectionKind Kind => ConnectionKind.Wsl;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
