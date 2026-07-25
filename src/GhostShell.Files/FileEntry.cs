namespace GhostShell.Files;

public sealed record FileEntry(
    FileLocation Location,
    FileEntryKind Kind,
    long? Size,
    DateTimeOffset? LastModifiedAt,
    FileVersion Version,
    bool IsHidden);
