namespace GhostShell.Application;

public sealed record ProcessMonitorQuery(
    int MaximumResults = ProcessMonitorQuery.DefaultMaximumResults,
    ProcessMonitorSort Sort = ProcessMonitorSort.CpuDescending)
{
    public const int DefaultMaximumResults = 250;
    public const int MaximumAllowedResults = 512;

    public bool IsValid =>
        MaximumResults is > 0 and <= MaximumAllowedResults
        && Enum.IsDefined(Sort);
}
