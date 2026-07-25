using System.Text.Json.Serialization;

namespace GhostShell.Core;

public readonly record struct QuickTerminalSettingsId
{
    [JsonConstructor]
    public QuickTerminalSettingsId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static QuickTerminalSettingsId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
