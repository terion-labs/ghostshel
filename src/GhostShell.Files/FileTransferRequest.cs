namespace GhostShell.Files;

/// <summary>
/// Describes a bounded copy or move within one provider profile. Cross-provider orchestration
/// composes bounded read and write operations outside the provider adapter.
/// </summary>
public sealed record FileTransferRequest
{
    public FileTransferRequest(
        FileLocation source,
        FileLocation destination,
        FileTransferKind kind,
        long maximumBytes,
        int bufferSize,
        FileMutationPrecondition destinationPrecondition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentNullException.ThrowIfNull(destinationPrecondition);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Source = source;
        Destination = destination;
        Kind = kind;
        MaximumBytes = maximumBytes;
        BufferSize = bufferSize;
        DestinationPrecondition = destinationPrecondition;
    }

    public FileLocation Source { get; }

    public FileLocation Destination { get; }

    public FileTransferKind Kind { get; }

    public long MaximumBytes { get; }

    public int BufferSize { get; }

    public FileMutationPrecondition DestinationPrecondition { get; }
}
