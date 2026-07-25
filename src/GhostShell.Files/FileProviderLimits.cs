namespace GhostShell.Files;

public sealed record FileProviderLimits
{
    public FileProviderLimits(
        int maximumListPageSize,
        long maximumReadBytes,
        long maximumWriteBytes,
        long maximumTransferBytes,
        int maximumBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumListPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWriteBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTransferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferSize);

        MaximumListPageSize = maximumListPageSize;
        MaximumReadBytes = maximumReadBytes;
        MaximumWriteBytes = maximumWriteBytes;
        MaximumTransferBytes = maximumTransferBytes;
        MaximumBufferSize = maximumBufferSize;
    }

    public int MaximumListPageSize { get; }

    public long MaximumReadBytes { get; }

    public long MaximumWriteBytes { get; }

    public long MaximumTransferBytes { get; }

    public int MaximumBufferSize { get; }
}
