using System.Text.Json.Serialization;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Exact read-only shape of the released stdio-only MCP profile schema. It is
/// accepted only at the persistence boundary and immediately upgraded in
/// memory to the transport-discriminated schema.
/// </summary>
internal sealed record McpServerProfileSchemaOne
{
    [JsonConstructor]
    public McpServerProfileSchemaOne(
        McpServerProfileId id,
        int schemaVersion,
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyList<McpServerEnvironmentVariable> environment,
        IReadOnlyList<string> enabledTools,
        bool isEnabled = true)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        EnabledTools = enabledTools;
        IsEnabled = isEnabled;
    }

    public McpServerProfileId Id { get; }

    public int SchemaVersion { get; }

    public string Name { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyList<McpServerEnvironmentVariable> Environment { get; }

    public IReadOnlyList<string> EnabledTools { get; }

    public bool IsEnabled { get; }
}
