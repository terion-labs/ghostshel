using System.Text;
using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// A bounded request for the command-line-free process projection exposed by
/// one exact local Process Monitor panel.
/// </summary>
public sealed record AgentProcessListRequest
{
    public const int MinimumLimit = 16;
    public const int DefaultLimit = 32;
    public const int MaximumLimit = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentProcessListRequest(
        PanelInstanceId panelId,
        int limit = DefaultLimit,
        ProcessMonitorSort sort = ProcessMonitorSort.CpuDescending)
    {
        RequirePanelId(panelId);
        if (limit is not (MinimumLimit or DefaultLimit or MaximumLimit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "An agent process list limit must be 16, 32, or 64.");
        }

        if (!Enum.IsDefined(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        PanelId = panelId;
        Limit = limit;
        Sort = sort;
    }

    public PanelInstanceId PanelId { get; }

    public int Limit { get; }

    public ProcessMonitorSort Sort { get; }

    private static void RequirePanelId(PanelInstanceId panelId)
    {
        var value = panelId.Value;
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An agent process request requires a printable panel identifier.",
                nameof(panelId));
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > 256)
            {
                throw new ArgumentException(
                    "An agent process request panel identifier is too long.",
                    nameof(panelId));
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An agent process request panel identifier must contain valid Unicode.",
                nameof(panelId),
                exception);
        }
    }
}
