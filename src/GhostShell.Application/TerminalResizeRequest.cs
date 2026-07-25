using GhostShell.Core;

namespace GhostShell.Application;

public sealed record TerminalResizeRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    ViewportDescriptor Viewport);
