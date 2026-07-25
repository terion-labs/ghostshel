using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal sealed record ProcessAgentIntent
{
    public ProcessAgentIntent(int limit, ProcessMonitorSort sort)
    {
        if (!ProcessAgentToolSet.IsAllowedLimit(limit))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!Enum.IsDefined(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        Limit = limit;
        Sort = sort;
    }

    public int Limit { get; }

    public ProcessMonitorSort Sort { get; }

    public string SortName =>
        Sort switch
        {
            ProcessMonitorSort.CpuDescending => "cpu_desc",
            ProcessMonitorSort.MemoryDescending => "memory_desc",
            ProcessMonitorSort.NameAscending => "name_asc",
            ProcessMonitorSort.ProcessIdAscending => "pid_asc",
            _ => throw new ArgumentOutOfRangeException(
                nameof(Sort),
                Sort,
                "The process sort is unsupported."),
        };
}

internal abstract record ProcessAgentIntentResult
{
    private ProcessAgentIntentResult()
    {
    }

    public sealed record Parsed(
        ProcessAgentIntent Intent,
        PanelInstanceId PanelId)
        : ProcessAgentIntentResult;

    public sealed record Rejected(
        string StableCode,
        string Message)
        : ProcessAgentIntentResult;
}
