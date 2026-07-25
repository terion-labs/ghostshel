using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureBrowserSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    BrowserAddress InitialAddress);
