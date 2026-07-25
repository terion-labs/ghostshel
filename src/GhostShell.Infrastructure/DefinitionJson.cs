using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhostShell.Infrastructure;

internal static class DefinitionJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(allowIntegerValues: false),
        },
    };
}
