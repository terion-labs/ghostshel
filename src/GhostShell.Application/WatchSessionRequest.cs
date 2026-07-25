using GhostShell.Core;

namespace GhostShell.Application;

public sealed record WatchSessionRequest(SessionId SessionId, long AfterSequence);
