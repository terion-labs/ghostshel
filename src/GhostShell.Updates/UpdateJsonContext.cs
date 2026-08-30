using System.Text.Json.Serialization;

namespace GhostShell.Updates;

[JsonSerializable(typeof(DistributionManifest))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
