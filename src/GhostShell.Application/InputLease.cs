using GhostShell.Core;

namespace GhostShell.Application;

public sealed record InputLease(
    InputLeaseId Id,
    SessionId SessionId,
    ActorDescriptor Holder,
    AttachmentId? AttachmentId,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision);
