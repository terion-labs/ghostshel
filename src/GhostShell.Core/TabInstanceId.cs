using System.Text.Json.Serialization;

namespace GhostShell.Core;

public readonly record struct TabInstanceId
{
    [JsonConstructor]
    public TabInstanceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static TabInstanceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
