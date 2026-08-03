using GhostShell.Application;

namespace GhostShell.Files;

public sealed partial class FilePanelClient : IFileContentMaterializer
{
    /// <summary>
    /// Temporary copies live in one owned directory rather than loose in the
    /// system temp root, so an interrupted process leaves something a human can
    /// recognize and delete.
    /// </summary>
    private const string TemporaryDirectoryName = "ghostshell-file-materialized";

    public async ValueTask<FilePanelResult<MaterializedFile>> MaterializeAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!TryResolve(location, out var registration, out var providerLocation, out var error))
        {
            return FilePanelResult<MaterializedFile>.Failure(error!);
        }

        // A local provider already has the file on disk. Copying it would waste
        // the space and, worse, hand back a stale snapshot of a file the user
        // can see changing in the same panel.
        if (registration!.Provider is ILocalFilePathSource localSource
            && localSource.TryGetLocalPath(providerLocation!) is { } localPath)
        {
            return FilePanelResult<MaterializedFile>.Success(
                new MaterializedFile(localPath, isTemporary: false));
        }

        var stat = await registration.Provider
            .StatAsync(new FileStatRequest(providerLocation!), cancellationToken)
            .ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FilePanelResult<MaterializedFile>.Failure(MapError(stat.Error!));
        }

        if (stat.Value!.Size is { } size && size > maximumBytes)
        {
            return Failure<MaterializedFile>(
                FilePanelErrorCode.LimitExceeded,
                "file_materialize_limit_exceeded",
                $"This file is {size} bytes; opening it here is limited to {maximumBytes} bytes.");
        }

        return await CopyToTemporaryFileAsync(
                registration,
                providerLocation!,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<FilePanelResult<MaterializedFile>> CopyToTemporaryFileAsync(
        FileProviderRegistration registration,
        FileLocation location,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), TemporaryDirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetRandomFileName());
        var lease = new MaterializedFile(path, isTemporary: true);
        try
        {
            var limits = registration.Provider.Capabilities.Limits;
            var chunk = Math.Max(1, Math.Min(limits.MaximumReadBytes, maximumBytes));
            var bufferSize = Math.Min(64 * 1024, limits.MaximumBufferSize);
            await using (var destination = new FileStream(
                             path,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                long offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = maximumBytes - offset;
                    if (remaining <= 0)
                    {
                        // The ceiling was reached with bytes still to come: the
                        // copy would be a prefix of the file, which for any
                        // structured format is worse than no file at all.
                        lease.Dispose();
                        return Failure<MaterializedFile>(
                            FilePanelErrorCode.LimitExceeded,
                            "file_materialize_limit_exceeded",
                            $"Opening this file here is limited to {maximumBytes} bytes.");
                    }

                    var read = await registration.Provider
                        .ReadAsync(
                            new FileReadRequest(
                                location,
                                offset,
                                Math.Min(chunk, remaining),
                                bufferSize),
                            destination,
                            progress: null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!read.IsSuccess)
                    {
                        lease.Dispose();
                        return FilePanelResult<MaterializedFile>.Failure(MapError(read.Error!));
                    }

                    var bytesRead = read.Value!.BytesRead;
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    offset += bytesRead;
                }
            }

            return FilePanelResult<MaterializedFile>.Success(lease);
        }
        catch (OperationCanceledException)
        {
            lease.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lease.Dispose();
            return Failure<MaterializedFile>(
                FilePanelErrorCode.IoFailure,
                "file_materialize_failed",
                "The file could not be copied to a temporary location.");
        }
    }
}

/// <summary>
/// Implemented by providers whose locations are real filesystem paths, so a
/// consumer needing a path gets the file itself rather than a copy. The
/// provider stays the authority on path composition and root confinement.
/// </summary>
internal interface ILocalFilePathSource
{
    string? TryGetLocalPath(FileLocation location);
}
