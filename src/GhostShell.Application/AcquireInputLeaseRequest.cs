using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AcquireInputLeaseRequest(
    SessionId SessionId,
    AttachmentId? AttachmentId,
    TimeSpan Duration);
