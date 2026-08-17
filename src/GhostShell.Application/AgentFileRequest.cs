using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Closed File Viewer operations. Callers can supply only validated relative path segments;
/// the trusted root, operation limits, and mutation semantics are resolved by the host.
/// </summary>
public abstract record AgentFileRequest
{
    private AgentFileRequest()
    {
    }

    public sealed record List(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath)
        : AgentFileRequest;

    public sealed record Search(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath,
        string Query,
        FilePanelDiscoveryScope Scope,
        int MaximumResults)
        : AgentFileRequest;

    public sealed record Stat(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath)
        : AgentFileRequest;

    public sealed record Read(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath)
        : AgentFileRequest;

    public sealed record AccessRead(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath)
        : AgentFileRequest;

    public sealed record Transfers(SessionId SessionId)
        : AgentFileRequest;

    public sealed record CreateDirectory(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath)
        : AgentFileRequest;

    public sealed record Move(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath,
        ImmutableArray<FilePanelPathSegment> DestinationRelativePath)
        : AgentFileRequest;

    public sealed record Delete(
        SessionId SessionId,
        ImmutableArray<FilePanelPathSegment> RelativePath,
        bool Recursive = false)
        : AgentFileRequest;
}
