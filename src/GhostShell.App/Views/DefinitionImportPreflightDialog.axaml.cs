using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.Application;

namespace GhostShell.App.Views;

public sealed partial class DefinitionImportPreflightDialog : Window
{
    public DefinitionImportPreflightDialog()
    {
        InitializeComponent();
    }

    public DefinitionImportPreflightDialog(DefinitionBundleImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        FileName = Path.GetFileName(plan.Path);
        Directory = Path.GetDirectoryName(plan.Path) ?? plan.Path;
        ModeLabel = plan.Mode == DefinitionImportMode.ReplaceExisting
            ? "REPLACE MATCHES"
            : "STOP ON CONFLICT";
        Summary = $"{plan.DefinitionCount} definitions · {plan.Conflicts.Count} identity conflicts · {plan.Issues.Count(issue => issue.IsBlocking)} blocking issues";
        CommitHint = plan.CanApply
            ? plan.Mode == DefinitionImportMode.ReplaceExisting
                ? "Matching identities will be replaced in one transaction. Secret values are never accepted in bundles."
                : "The bundle will be committed in one transaction. Existing definitions remain unchanged."
            : "Resolve blocking issues or choose a different file. This import cannot be applied.";
        CanApply = plan.CanApply;
        Issues = plan.Issues.Select(DefinitionImportIssueItem.From).ToArray();
        HasIssues = Issues.Count > 0;
        HasNoIssues = !HasIssues;
        InitializeComponent();
        DataContext = this;
    }

    public string FileName { get; } = "Definition bundle";

    public string Directory { get; } = string.Empty;

    public string ModeLabel { get; } = string.Empty;

    public string Summary { get; } = string.Empty;

    public string CommitHint { get; } = string.Empty;

    public bool CanApply { get; }

    public bool HasIssues { get; }

    public bool HasNoIssues { get; } = true;

    public IReadOnlyList<DefinitionImportIssueItem> Issues { get; } = [];

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(CanApply);
    }
}

public sealed record DefinitionImportIssueItem(string Label, string Heading, string Message)
{
    public static DefinitionImportIssueItem From(DefinitionImportIssue issue) => new(
        issue.IsBlocking ? "BLOCKING" : "NOTICE",
        Humanize(issue.Code),
        issue.Message);

    private static string Humanize(DefinitionImportIssueCode code) => code switch
    {
        DefinitionImportIssueCode.ExistingIdentity => "Existing identity",
        DefinitionImportIssueCode.MissingDependency => "Missing dependency",
        DefinitionImportIssueCode.UnsupportedKind => "Unsupported definition kind",
        DefinitionImportIssueCode.UnsupportedSchema => "Unsupported schema version",
        DefinitionImportIssueCode.UnsafePayload => "Unsafe payload rejected",
        DefinitionImportIssueCode.DuplicateIdentity => "Duplicate identity",
        DefinitionImportIssueCode.InvalidPayload => "Invalid definition payload",
        DefinitionImportIssueCode.ImportedMcpProfileDisabled =>
            "MCP server disabled for review",
        _ => "Invalid definition bundle",
    };
}
