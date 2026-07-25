using System.Collections.Immutable;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal abstract record FileAgentIntent
{
    private FileAgentIntent()
    {
    }

    public abstract ImmutableArray<FilePanelPathSegment> RelativePath { get; }

    public sealed record List : FileAgentIntent
    {
        public List(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = pathSegments.ToImmutableArray();
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Stat : FileAgentIntent
    {
        public Stat(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = pathSegments.ToImmutableArray();
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Read : FileAgentIntent
    {
        public Read(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = pathSegments.ToImmutableArray();
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record CreateDirectory : FileAgentIntent
    {
        public CreateDirectory(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = pathSegments.ToImmutableArray();
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Delete : FileAgentIntent
    {
        public Delete(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = pathSegments.ToImmutableArray();
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }
}

internal abstract record FileAgentIntentResult
{
    private FileAgentIntentResult()
    {
    }

    public sealed record Parsed(
        FileAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : FileAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : FileAgentIntentResult;
}
