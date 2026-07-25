using GhostShell.Core;

namespace GhostShell.Application;

public sealed record TerminalWriteRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    string Text);
