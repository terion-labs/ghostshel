using GhostShell.Core;

namespace GhostShell.Application;

public sealed record DetachSessionRequest(AttachmentId AttachmentId, SessionId SessionId);
