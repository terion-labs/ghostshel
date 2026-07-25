using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Reusable panel geometry. Imported and designer-produced instances must pass
/// <see cref="LayoutValidator"/> before they are persisted or opened.
/// </summary>
public sealed record LayoutDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public LayoutDefinition(
        LayoutId id,
        int schemaVersion,
        string name,
        LayoutGrid grid,
        IReadOnlyList<LayoutSlotDefinition> slots)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Slots = Array.AsReadOnly(slots?.ToArray() ?? throw new ArgumentNullException(nameof(slots)));
    }

    public static DefinitionKind Kind => DefinitionKind.Layout;

    public LayoutId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public LayoutGrid Grid { get; }

    /// <summary>The list order is the default keyboard and accessibility traversal order.</summary>
    public IReadOnlyList<LayoutSlotDefinition> Slots { get; }
}
