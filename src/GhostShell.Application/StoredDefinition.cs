using GhostShell.Core;

namespace GhostShell.Application;

public sealed record StoredDefinition<TDefinition>(
    TDefinition Value,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
    where TDefinition : IDurableDefinition;
