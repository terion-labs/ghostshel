using GhostShell.Core;

namespace GhostShell.Application;

public sealed record BrowserNavigateRequest(
    SessionId SessionId,
    BrowserAddress Address);
