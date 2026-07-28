using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Asks a live terminal to adopt new typography and palette without restarting.
/// </summary>
public sealed record UpdateTerminalRenderProfileRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    TerminalRenderProfileSnapshot RenderProfile);
