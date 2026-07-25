using GhostShell.Application;

namespace GhostShell.App;

/// <summary>
/// A preflighted, immutable import proposal. Payload JSON remains internal; presentation code gets
/// only bounded counts and structured issues to display before explicit confirmation.
/// </summary>
public sealed class DefinitionBundleImportPlan
{
    internal DefinitionBundleImportPlan(
        string path,
        DefinitionImportPreflight preflight)
    {
        Path = path;
        Mode = preflight.Mode;
        DefinitionCount = preflight.Bundle.Definitions.Count;
        Issues = Array.AsReadOnly(preflight.Issues.ToArray());
        Conflicts = Array.AsReadOnly(Issues
            .Where(issue => issue.Code == DefinitionImportIssueCode.ExistingIdentity)
            .ToArray());
        CanApply = Issues.All(issue => !issue.IsBlocking);
        Preflight = preflight;
    }

    internal DefinitionImportPreflight Preflight { get; }

    public string Path { get; }

    public DefinitionImportMode Mode { get; }

    public int DefinitionCount { get; }

    public IReadOnlyList<DefinitionImportIssue> Issues { get; }

    public IReadOnlyList<DefinitionImportIssue> Conflicts { get; }

    public bool CanApply { get; }
}
