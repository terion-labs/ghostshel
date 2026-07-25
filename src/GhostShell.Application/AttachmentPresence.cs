using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AttachmentPresence(
    AttachmentId Id,
    SessionId SessionId,
    ClientId ClientId,
    AttachmentKind Kind,
    ViewportDescriptor Viewport,
    DateTimeOffset AttachedAtUtc);
