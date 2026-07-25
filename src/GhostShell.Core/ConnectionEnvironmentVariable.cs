using System.Text.RegularExpressions;

namespace GhostShell.Core;

public sealed partial record ConnectionEnvironmentVariable
{
    public ConnectionEnvironmentVariable(string name, ConnectionEnvironmentValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var candidate = name ?? string.Empty;
        if (!PortableName().IsMatch(candidate))
        {
            throw new ArgumentException(
                "Environment variable names must use portable shell identifier syntax.",
                nameof(name));
        }

        Name = candidate;
        Value = value;
    }

    public string Name { get; }

    public ConnectionEnvironmentValue Value { get; }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableName();
}
