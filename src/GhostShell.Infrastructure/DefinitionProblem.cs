using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal enum DefinitionProblemKind
{
    InvalidDefinition,
    UnsupportedKind,
    UnsupportedSchema,
    UnsafePayload,
    MissingDependency,
    DependencyConflict,
    StorageFailure,
}

internal sealed record DefinitionProblem(
    DefinitionProblemKind Kind,
    string Message,
    DefinitionKey? Definition = null);
