using GhostShell.Core;

namespace GhostShell.Application;

public sealed record TerminalEnterRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public sealed record TerminalInterruptRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public sealed record TerminalWaitForTextRequest(
    SessionId SessionId,
    TerminalWaitForTextInput Wait);

public sealed record TerminalWaitForChangeRequest(
    SessionId SessionId,
    TerminalWaitForChangeInput Wait);

public sealed record TerminalWaitForStableRequest(
    SessionId SessionId,
    TerminalWaitForStableInput Wait);
