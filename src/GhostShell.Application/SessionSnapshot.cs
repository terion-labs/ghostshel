namespace GhostShell.Application;

public sealed record SessionSnapshot(
    SessionDescriptor Descriptor,
    long LastSequence,
    IReadOnlyList<AttachmentPresence> Attachments,
    InputLease? InputLease);
