using System.Text.Json.Serialization;

namespace GhostShell.Core;

public readonly record struct AttachmentId
{
    [JsonConstructor]
    public AttachmentId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AttachmentId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
