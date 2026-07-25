namespace GhostShell.Files;

public sealed record FileTransferProgress(
    FileTransferStage Stage,
    long BytesTransferred,
    long? TotalBytes);
