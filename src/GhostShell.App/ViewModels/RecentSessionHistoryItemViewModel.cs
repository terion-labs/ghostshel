using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record RecentSessionHistoryItemViewModel(
    RecentSessionRecord Record,
    bool CanOpen,
    DateTimeOffset ObservedAt)
{
    public SessionId SessionId => Record.SessionId;

    public DefinitionKey SourceDefinition => Record.SourceDefinition;

    public PanelKind PanelKind => Record.Kind;

    public RecentSessionOutcome Outcome => Record.Outcome;

    public string Title => Record.Title;

    public string PanelKindName => PanelKindLabel(PanelKind);

    public string OutcomeName => OutcomeLabel(Outcome);

    public string Detail => $"{PanelKindName} · {OutcomeName}";

    public string SourceKind => SourceKindLabel(SourceDefinition.Kind);

    public string SourceIdentifier => SourceDefinition.Value;

    public string SessionIdentifier => SessionId.Value;

    public string LastUsed => RelativeTime(Record.LastUsedAt, ObservedAt);

    public string Started => FormatTimestamp(Record.StartedAt);

    public string Ended => Record.EndedAt is { } endedAt
        ? FormatTimestamp(endedAt)
        : "In progress";

    public string Duration => Record.EndedAt is { } endedAt
        ? FormatDuration(endedAt - Record.StartedAt)
        : "In progress";

    public string ReopenStatus => CanOpen
        ? "Reopening launches the current saved definition; history retains metadata, not a session snapshot."
        : "The current saved definition no longer exists or is unavailable on this platform; metadata remains available for review.";

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(0, (int)duration.TotalSeconds)} sec";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{(int)duration.TotalMinutes} min";
        }

        return duration < TimeSpan.FromDays(1)
            ? $"{(int)duration.TotalHours} hr {duration.Minutes} min"
            : $"{(int)duration.TotalDays} d {duration.Hours} hr";
    }

    private static string RelativeTime(DateTimeOffset timestamp, DateTimeOffset observedAt)
    {
        var age = observedAt.ToUniversalTime() - timestamp.ToUniversalTime();
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)age.TotalHours)} hr ago";
        }

        return age < TimeSpan.FromDays(7)
            ? $"{Math.Max(1, (int)age.TotalDays)} d ago"
            : timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    private static string PanelKindLabel(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "Terminal",
        PanelKind.FileViewer => "File Viewer",
        PanelKind.Browser => "Browser",
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string OutcomeLabel(RecentSessionOutcome outcome) => outcome switch
    {
        RecentSessionOutcome.Active => "Active",
        RecentSessionOutcome.GracefullyClosed => "Closed",
        RecentSessionOutcome.ForceTerminated => "Force terminated",
        RecentSessionOutcome.Failed => "Failed",
        RecentSessionOutcome.Cancelled => "Cancelled",
        RecentSessionOutcome.Interrupted => "Interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string SourceKindLabel(DefinitionKind kind) => kind switch
    {
        var value when value == ConnectionProfile.Kind => "CONNECTION",
        var value when value == ScreenDefinition.Kind => "SCREEN",
        var value when value == WorkspaceDefinition.Kind => "WORKSPACE",
        _ => kind.Value.ToUpperInvariant(),
    };
}
