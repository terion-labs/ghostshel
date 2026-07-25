using System.Text.Json.Serialization;

namespace GhostShell.Core;

public sealed record WorkspaceDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultIcon = "workspace";

    [JsonConstructor]
    public WorkspaceDefinition(
        WorkspaceId id,
        int schemaVersion,
        string name,
        string? description,
        string? accent,
        IReadOnlyList<WorkspaceEntry> entries,
        AgentPolicy? agentPolicyOverride = null,
        string? icon = null)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Accent = string.IsNullOrWhiteSpace(accent) ? null : accent.Trim();
        Entries = Array.AsReadOnly(entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries)));
        AgentPolicyOverride = agentPolicyOverride;
        Icon = string.IsNullOrWhiteSpace(icon) ? DefaultIcon : icon.Trim();
    }

    public static DefinitionKind Kind => DefinitionKind.Workspace;

    public WorkspaceId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public string? Description { get; }

    public string? Accent { get; }

    /// <summary>
    /// A renderer-neutral semantic icon identifier. Views map this stable value to
    /// the icon set available on the current platform.
    /// </summary>
    public string Icon { get; }

    /// <summary>The stored sequence is the launcher's visible and keyboard traversal order.</summary>
    public IReadOnlyList<WorkspaceEntry> Entries { get; }

    public AgentPolicy? AgentPolicyOverride { get; }

    public WorkspaceDefinition MoveEntry(WorkspaceEntryId entryId, int destinationIndex)
    {
        if (destinationIndex < 0 || destinationIndex >= Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }

        var sourceIndex = Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.Id == entryId)
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (sourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryId), entryId, "The entry does not belong to this workspace.");
        }

        var reordered = Entries.ToList();
        var entry = reordered[sourceIndex];
        reordered.RemoveAt(sourceIndex);
        reordered.Insert(destinationIndex, entry);

        return new(
            Id,
            SchemaVersion,
            Name,
            Description,
            Accent,
            reordered,
            AgentPolicyOverride,
            Icon);
    }

    public static bool IsValidIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon) || icon.Length > 48 || !IsLowerAsciiLetter(icon[0]))
        {
            return false;
        }

        return icon.All(character =>
            IsLowerAsciiLetter(character)
            || character is >= '0' and <= '9'
            || character is '-' or '.');
    }

    private static bool IsLowerAsciiLetter(char character) => character is >= 'a' and <= 'z';
}
