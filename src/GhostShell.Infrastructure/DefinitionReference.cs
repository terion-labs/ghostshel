using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal sealed record DefinitionReference(DefinitionKey Target, string Role);
