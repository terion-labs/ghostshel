using System.Collections.Immutable;

namespace GhostShell.Core;

public sealed class CommandDefinition
{
    private readonly Func<CommandInvocation, bool> _availability;

    public CommandDefinition(
        CommandId id,
        string title,
        string category,
        CommandContext contexts,
        CommandParameterSchema? parameters = null,
        IEnumerable<CommandBinding>? defaultBindings = null,
        Func<CommandInvocation, bool>? availability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        Id = id;
        Title = title;
        Category = category;
        Contexts = CommandContextRules.Require(contexts, nameof(contexts));
        Parameters = parameters ?? CommandParameterSchema.None;
        DefaultBindings = defaultBindings?.ToImmutableArray() ?? [];
        _availability = availability ?? AlwaysAvailable;
    }

    public CommandId Id { get; }

    public string Title { get; }

    public string Category { get; }

    public CommandContext Contexts { get; }

    public CommandParameterSchema Parameters { get; }

    public ImmutableArray<CommandBinding> DefaultBindings { get; }

    public bool IsAvailable(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var contextMatches = (Contexts & CommandContext.Global) != 0
            || (Contexts & invocation.ActiveContexts) != 0;

        return contextMatches
            && Parameters.Validate(invocation.Arguments).IsEmpty
            && _availability(invocation);
    }

    private static bool AlwaysAvailable(CommandInvocation _) => true;
}
