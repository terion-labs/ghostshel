namespace GhostShell.Files;

/// <summary>
/// Implements the provider-neutral invariants shared by SFTP and FTP while leaving connection,
/// metadata parsing, transport security, and server feature handling in protocol adapters.
/// </summary>
public abstract partial class RemoteHierarchicalFileProvider : IFileProvider
{
    private readonly IRemoteHierarchicalFileSessionFactory _sessions;
    private readonly string _remoteRoot;
    private readonly IReadOnlyList<string> _remoteRootSegments;
    private readonly string _protocolName;
    private readonly bool _allowBackslashSegments;
    private readonly Func<string, bool>? _additionalNameValidator;
    private readonly RemoteMetadataReconnectPolicy _metadataReconnectPolicy;
    private readonly FilePageCursorStore<RemotePageCursor> _pageCursors = new();

    private protected RemoteHierarchicalFileProvider(
        IRemoteHierarchicalFileSessionFactory sessions,
        FileProviderProfileId profileId,
        FileAuthority authority,
        string remoteRoot,
        string protocolName,
        bool allowBackslashSegments,
        FileNameComparison nameComparison,
        RemoteMetadataReconnectPolicy metadataReconnectPolicy,
        FileProviderLimits limits,
        Func<string, bool>? additionalNameValidator = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolName);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(metadataReconnectPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadataReconnectPolicy),
                metadataReconnectPolicy,
                "The reconnect policy is invalid.");
        }

        if (!Enum.IsDefined(nameComparison))
        {
            throw new ArgumentOutOfRangeException(
                nameof(nameComparison),
                nameComparison,
                "The file-name comparison policy is invalid.");
        }

        _sessions = sessions;
        _remoteRoot = NormalizeRemoteRoot(
            remoteRoot,
            allowBackslashSegments,
            additionalNameValidator);
        _remoteRootSegments = _remoteRoot == "/"
            ? []
            : _remoteRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _protocolName = protocolName;
        _allowBackslashSegments = allowBackslashSegments;
        _additionalNameValidator = additionalNameValidator;
        _metadataReconnectPolicy = metadataReconnectPolicy;
        ProfileId = profileId;
        Authority = authority;
        Capabilities = new FileProviderCapabilities(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead
            | FileProviderCapability.StreamingWrite
            | FileProviderCapability.CreateDirectory
            | FileProviderCapability.Rename
            | FileProviderCapability.Copy
            | FileProviderCapability.Move
            | FileProviderCapability.Delete
            | FileProviderCapability.Pagination,
            nameComparison,
            limits);
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public FileProviderCapabilities Capabilities { get; }

    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => ListCoreAsync(request, token),
            retryMetadata: true,
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => StatCoreAsync(request.Location, token),
            retryMetadata: true,
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        return ExecuteAsync(
            token => ReadCoreAsync(request, destination, progress, token),
            retryMetadata: false,
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        return ExecuteAsync(
            token => WriteCoreAsync(request, source, progress, token),
            retryMetadata: false,
            cancellationToken,
            request.Precondition);
    }

    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => CreateDirectoryCoreAsync(request, token),
            retryMetadata: false,
            cancellationToken,
            request.Precondition);
    }

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => RenameCoreAsync(request, token),
            retryMetadata: false,
            cancellationToken,
            request.DestinationPrecondition);
    }

    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => TransferCoreAsync(request, progress, token),
            retryMetadata: false,
            cancellationToken,
            request.DestinationPrecondition);
    }

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => DeleteCoreAsync(request, token),
            retryMetadata: false,
            cancellationToken,
            request.Precondition);
    }

    private async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        bool retryMetadata,
        CancellationToken cancellationToken,
        FileMutationPrecondition? mutationPrecondition = null)
    {
        var attempts = retryMetadata
            && _metadataReconnectPolicy == RemoteMetadataReconnectPolicy.RetryOnce
                ? 2
                : 1;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure<T>(
                    FileProviderErrorCode.Cancelled,
                    $"The {_protocolName} operation was cancelled.");
            }
            catch (RemoteFileSessionException exception)
                when (attempt + 1 < attempts && exception.Retryable)
            {
                // A fresh session is created by the next attempt. Mutations and streamed reads
                // never reach this branch, because replaying them could duplicate side effects.
            }
            catch (RemoteFileSessionException exception)
            {
                return FileProviderResult<T>.Failure(MapRemoteError(exception, mutationPrecondition));
            }
            catch (EndOfStreamException)
            {
                return Failure<T>(
                    FileProviderErrorCode.UnexpectedEndOfStream,
                    $"The {_protocolName} stream ended before the declared byte count.");
            }
            catch (IOException)
            {
                return Failure<T>(
                    FileProviderErrorCode.IoFailure,
                    $"The {_protocolName} transport failed.",
                    retryable: true);
            }
        }
    }

    private FileProviderError MapRemoteError(
        RemoteFileSessionException exception,
        FileMutationPrecondition? mutationPrecondition)
    {
        var code = exception.Code switch
        {
            RemoteFileSessionErrorCode.InvalidConfiguration => FileProviderErrorCode.InvalidLocation,
            RemoteFileSessionErrorCode.AuthenticationFailed => FileProviderErrorCode.AccessDenied,
            RemoteFileSessionErrorCode.HostKeyUnknown => FileProviderErrorCode.HostKeyUnknown,
            RemoteFileSessionErrorCode.HostKeyChanged => FileProviderErrorCode.HostKeyChanged,
            RemoteFileSessionErrorCode.HostKeyStoreInvalid => FileProviderErrorCode.HostKeyStoreInvalid,
            RemoteFileSessionErrorCode.CertificateRejected => FileProviderErrorCode.AccessDenied,
            RemoteFileSessionErrorCode.SecureTransportUnavailable => FileProviderErrorCode.AccessDenied,
            RemoteFileSessionErrorCode.NotFound => FileProviderErrorCode.NotFound,
            RemoteFileSessionErrorCode.AlreadyExists
                when mutationPrecondition is FileMutationPrecondition.MustNotExist =>
                    FileProviderErrorCode.Conflict,
            RemoteFileSessionErrorCode.AlreadyExists => FileProviderErrorCode.AlreadyExists,
            RemoteFileSessionErrorCode.AccessDenied => FileProviderErrorCode.AccessDenied,
            RemoteFileSessionErrorCode.NotDirectory => FileProviderErrorCode.NotDirectory,
            RemoteFileSessionErrorCode.IsDirectory => FileProviderErrorCode.IsDirectory,
            RemoteFileSessionErrorCode.DirectoryNotEmpty => FileProviderErrorCode.DirectoryNotEmpty,
            RemoteFileSessionErrorCode.LimitExceeded => FileProviderErrorCode.LimitExceeded,
            RemoteFileSessionErrorCode.LinkNotAllowed => FileProviderErrorCode.LinkNotAllowed,
            RemoteFileSessionErrorCode.Unsupported => FileProviderErrorCode.UnsupportedCapability,
            RemoteFileSessionErrorCode.InvalidName => FileProviderErrorCode.InvalidName,
            RemoteFileSessionErrorCode.Transient => FileProviderErrorCode.IoFailure,
            _ => FileProviderErrorCode.IoFailure,
        };
        return FileProviderError.Create(code, exception.Message, exception.Retryable);
    }

    private static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        RemoteFileProviderUtilities.Failure<T>(code, message, retryable);

    private static string NormalizeRemoteRoot(
        string remoteRoot,
        bool allowBackslashSegments,
        Func<string, bool>? additionalNameValidator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRoot);
        if (!remoteRoot.StartsWith("/", StringComparison.Ordinal)
            || remoteRoot.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A remote root must be an absolute path without control characters.",
                nameof(remoteRoot));
        }

        var components = remoteRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".."))
        {
            throw new ArgumentException(
                "A remote root cannot contain traversal components.",
                nameof(remoteRoot));
        }

        if (!allowBackslashSegments
            && components.Any(component => component.Contains('\\') || HasBoundaryWhitespace(component)))
        {
            throw new ArgumentException(
                "The remote root contains a name unsupported by this protocol adapter.",
                nameof(remoteRoot));
        }

        if (additionalNameValidator is not null
            && components.Any(component => !additionalNameValidator(component)))
        {
            throw new ArgumentException(
                "The remote root contains a name unsupported by this protocol adapter.",
                nameof(remoteRoot));
        }

        return components.Length == 0 ? "/" : $"/{string.Join('/', components)}";
    }

    private static bool HasBoundaryWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private FileProviderResult<ResolvedRemotePath> Resolve(FileLocation location)
    {
        if (location.ProviderProfileId != ProfileId
            || location.Authority != Authority
            || location.Address is not FileLocationAddress.Hierarchical hierarchical)
        {
            return Failure<ResolvedRemotePath>(
                FileProviderErrorCode.InvalidLocation,
                $"The location does not belong to this {_protocolName} provider.");
        }

        foreach (var segment in hierarchical.Path.Segments)
        {
            if (segment.Value.Any(char.IsControl)
                || (!_allowBackslashSegments
                    && (segment.Value.Contains('\\') || HasBoundaryWhitespace(segment.Value)))
                || (_additionalNameValidator is not null
                    && !_additionalNameValidator(segment.Value)))
            {
                return Failure<ResolvedRemotePath>(
                    FileProviderErrorCode.InvalidName,
                    $"The path contains a name unsupported by this {_protocolName} provider.");
            }
        }

        var relative = hierarchical.Path.Segments.Select(segment => segment.Value).ToArray();
        var remotePath = relative.Length == 0
            ? _remoteRoot
            : _remoteRoot == "/"
                ? $"/{string.Join('/', relative)}"
                : $"{_remoteRoot}/{string.Join('/', relative)}";
        return FileProviderResult<ResolvedRemotePath>.Success(
            new ResolvedRemotePath(location, hierarchical.Path, remotePath, relative));
    }

    private string ChildRemotePath(string parent, string name) =>
        parent == "/" ? $"/{name}" : $"{parent}/{name}";

    private string TemporarySibling(ResolvedRemotePath destination) =>
        ChildRemotePath(
            RemoteParent(destination.RemotePath),
            $".ghostshell-{Guid.NewGuid():N}.tmp");

    private static string RemoteParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private sealed record ResolvedRemotePath(
        FileLocation Location,
        FilePath Path,
        string RemotePath,
        IReadOnlyList<string> RelativeSegments);

    private sealed record RemotePageCursor(string Scope, int Offset);
}
