using System.Collections.Immutable;

namespace GhostShell.Core;

public sealed record CommandInvocation
{
    public CommandInvocation(
        CommandContext activeContexts,
        IEnumerable<KeyValuePair<string, string>>? arguments = null,
        IEnumerable<KeyValuePair<string, bool>>? state = null)
    {
        ActiveContexts = CommandContextRules.Require(activeContexts, nameof(activeContexts));
        Arguments = arguments?.ToImmutableDictionary(StringComparer.Ordinal)
            ?? ImmutableDictionary<string, string>.Empty;
        State = state?.ToImmutableDictionary(StringComparer.Ordinal)
            ?? ImmutableDictionary<string, bool>.Empty;
    }

    public CommandContext ActiveContexts { get; }

    public ImmutableDictionary<string, string> Arguments { get; }

    public ImmutableDictionary<string, bool> State { get; }

    public bool HasState(string name) => State.TryGetValue(name, out var value) && value;
}
