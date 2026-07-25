using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AttachTerminalRendererRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    NativeRendererHost RendererHost);
