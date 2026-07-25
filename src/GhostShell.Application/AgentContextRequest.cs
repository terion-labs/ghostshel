using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AgentContextRequest
{
    public const int DefaultMaximumPanelCount = 64;
    public const int MaximumAllowedPanelCount = 256;

    public AgentContextRequest(
        AgentTarget target,
        int maximumPanelCount = DefaultMaximumPanelCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (maximumPanelCount is < 1 or > MaximumAllowedPanelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPanelCount),
                maximumPanelCount,
                $"The panel limit must be between 1 and {MaximumAllowedPanelCount}.");
        }

        Target = target;
        MaximumPanelCount = maximumPanelCount;
    }

    public AgentTarget Target { get; }

    public int MaximumPanelCount { get; }
}
