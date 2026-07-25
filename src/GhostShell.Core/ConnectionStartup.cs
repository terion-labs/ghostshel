namespace GhostShell.Core;

public sealed record ConnectionStartup
{
    public ConnectionStartup(
        string? directory = null,
        IReadOnlyList<ConnectionEnvironmentVariable>? environment = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        Environment = Array.AsReadOnly(environment?.ToArray() ?? []);

        var duplicate = Environment
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Environment variable '{duplicate.Key}' is defined more than once.",
                nameof(environment));
        }
    }

    public static ConnectionStartup Default { get; } = new();

    public string? Directory { get; }

    public IReadOnlyList<ConnectionEnvironmentVariable> Environment { get; }
}
