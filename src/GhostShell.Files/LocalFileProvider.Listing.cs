namespace GhostShell.Files;

public abstract partial class LocalFileProvider
{
    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            async token =>
            {
                if (request.PageSize > Capabilities.Limits.MaximumListPageSize)
                {
                    return Failure<FilePage>(
                        FileProviderErrorCode.LimitExceeded,
                        "The requested page size exceeds the provider limit.");
                }

                var resolved = ResolveLocation(request.Location, allowLeafLink: false);
                if (!resolved.IsSuccess)
                {
                    return FileProviderResult<FilePage>.Failure(resolved.Error!);
                }

                var directoryEntry = ReadEntry(resolved.Value!);
                if (!directoryEntry.IsSuccess)
                {
                    return FileProviderResult<FilePage>.Failure(directoryEntry.Error!);
                }

                if (directoryEntry.Value!.Kind != FileEntryKind.Directory)
                {
                    return Failure<FilePage>(
                        FileProviderErrorCode.NotDirectory,
                        "Only directories can be listed.");
                }

                var offsetResult = DecodePageOffset(request.ContinuationToken);
                if (!offsetResult.IsSuccess)
                {
                    return FileProviderResult<FilePage>.Failure(offsetResult.Error!);
                }

                var entries = new List<FileEntry>(request.PageSize);
                var offset = offsetResult.Value;
                var index = 0;
                var hasMore = false;

                foreach (var childPath in Directory.EnumerateFileSystemEntries(resolved.Value!.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (index++ < offset)
                    {
                        continue;
                    }

                    if (entries.Count == request.PageSize)
                    {
                        hasMore = true;
                        break;
                    }

                    var childName = new FilePathSegment(Path.GetFileName(childPath));
                    var childLocation = request.Location.WithVersion(null).Child(childName);
                    var childResolved = new ResolvedLocalLocation(
                        childLocation,
                        childLocation.Path,
                        childPath);
                    var child = ReadEntry(childResolved);
                    if (!child.IsSuccess)
                    {
                        return FileProviderResult<FilePage>.Failure(child.Error!);
                    }

                    entries.Add(child.Value!);
                }

                FilePageToken? nextToken = hasMore
                    ? EncodePageOffset(offset + entries.Count)
                    : null;
                await Task.CompletedTask.ConfigureAwait(false);
                return FileProviderResult<FilePage>.Success(new FilePage(entries, nextToken));
            },
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteFileSystemOperationAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                var resolved = ResolveLocation(request.Location, allowLeafLink: true);
                return ValueTask.FromResult(!resolved.IsSuccess
                    ? FileProviderResult<FileEntry>.Failure(resolved.Error!)
                    : ReadEntry(resolved.Value!));
            },
            cancellationToken);
    }

    private static FileProviderResult<int> DecodePageOffset(FilePageToken? token)
    {
        if (token is null)
        {
            return FileProviderResult<int>.Success(0);
        }

        try
        {
            var bytes = Convert.FromBase64String(token.Value.Value);
            if (bytes.Length != sizeof(int))
            {
                return InvalidPageToken();
            }

            var offset = BitConverter.ToInt32(bytes);
            return offset < 0
                ? InvalidPageToken()
                : FileProviderResult<int>.Success(offset);
        }
        catch (FormatException)
        {
            return InvalidPageToken();
        }

        static FileProviderResult<int> InvalidPageToken() => Failure<int>(
            FileProviderErrorCode.InvalidLocation,
            "The list continuation token is invalid.");
    }

    private static FilePageToken EncodePageOffset(int offset) =>
        new(Convert.ToBase64String(BitConverter.GetBytes(offset)));
}
