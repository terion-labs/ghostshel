using System.Text.Json.Serialization;
using GhostShell.Application;

namespace GhostShell.Docker;

/// <summary>Static contracts for bounded Docker provider projections.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DockerPanelSnapshot))]
[JsonSerializable(typeof(DockerInspectionSnapshot))]
[JsonSerializable(typeof(DockerContainerLogPage))]
[JsonSerializable(typeof(DockerFilePage))]
[JsonSerializable(typeof(DockerFileEntry))]
[JsonSerializable(typeof(DockerFileSnapshot))]
[JsonSerializable(typeof(DockerEngineSummary))]
internal sealed partial class DockerProjectionJsonContext : JsonSerializerContext;
