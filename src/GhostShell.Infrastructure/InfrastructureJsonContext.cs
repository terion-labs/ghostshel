using System.Text.Json.Serialization;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

/// <summary>Static JSON contracts shared by platform infrastructure.</summary>
[JsonSerializable(typeof(SecretMetadata))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;
