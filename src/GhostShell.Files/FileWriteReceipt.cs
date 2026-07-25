namespace GhostShell.Files;

public sealed record FileWriteReceipt(
    FileEntry Destination,
    long BytesWritten,
    bool ReplacedExisting);
