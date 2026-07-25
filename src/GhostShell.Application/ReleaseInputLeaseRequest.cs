using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ReleaseInputLeaseRequest(SessionId SessionId, InputLeaseId LeaseId);
