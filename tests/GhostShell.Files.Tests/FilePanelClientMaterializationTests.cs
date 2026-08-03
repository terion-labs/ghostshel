using GhostShell.Application;

namespace GhostShell.Files.Tests;

public sealed class FilePanelClientMaterializationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ghostshell-materialize").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_local_file_materializes_in_place_without_a_copy()
    {
        var path = Path.Combine(_root, "database.db");
        await File.WriteAllTextAsync(path, "SQLite format 3\0payload");
        var client = CreateClient();

        var result = await ((IFileContentMaterializer)client).MaterializeAsync(
            Location("database.db"),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var lease = result.Value!;
        Assert.Equal(path, lease.Path);
        lease.Dispose();
        // Disposing a lease over a local file must never delete the user's file.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_large_local_file_still_materializes_because_nothing_is_copied()
    {
        // The ceiling bounds copying, not opening: a database that is already on
        // this machine is opened where it lies, and the engine reads the pages
        // it needs. Refusing it by size would be a limit invented for its own
        // sake.
        var path = Path.Combine(_root, "big.db");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var client = CreateClient();

        var result = await ((IFileContentMaterializer)client).MaterializeAsync(
            Location("big.db"),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var lease = result.Value!;
        Assert.Equal(path, lease.Path);
    }

    [Fact]
    public async Task A_remote_file_over_the_ceiling_is_refused_rather_than_truncated()
    {
        var provider = new RemoteBytesProvider(new byte[4096]);
        var client = ClientFor(provider);

        var result = await ((IFileContentMaterializer)client).MaterializeAsync(
            new FilePanelLocation(
                provider.ProfileId.Value,
                "fixture",
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments([new FilePanelPathSegment("big.db")]))),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.LimitExceeded, result.Error!.Code);
        Assert.Empty(TemporaryCopies());
    }

    [Fact]
    public async Task A_remote_file_is_copied_whole_into_a_temporary_file_and_deleted_with_the_lease()
    {
        var content = new byte[2500];
        Random.Shared.NextBytes(content);
        var provider = new RemoteBytesProvider(content);
        var client = ClientFor(provider);

        var result = await ((IFileContentMaterializer)client).MaterializeAsync(
            new FilePanelLocation(
                provider.ProfileId.Value,
                "fixture",
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments([new FilePanelPathSegment("remote.db")]))),
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var lease = result.Value!;
        // Whole, not the first chunk: the provider caps each read at 1 KiB, so a
        // single read would have produced a truncated — corrupt — database.
        Assert.Equal(content, await File.ReadAllBytesAsync(lease.Path));
        lease.Dispose();
        Assert.False(File.Exists(lease.Path));
    }

    [Fact]
    public async Task An_unknown_profile_fails_without_touching_the_filesystem()
    {
        var client = CreateClient();

        var result = await ((IFileContentMaterializer)client).MaterializeAsync(
            new FilePanelLocation(
                "absent-profile",
                "fixture",
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments([new FilePanelPathSegment("database.db")]))),
            maximumBytes: 1024,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.UnknownProfile, result.Error!.Code);
    }

    private FilePanelClient CreateClient()
    {
        var provider = LocalFileProvider.CreateForCurrentPlatform(
            new LocalFileProviderOptions(
                new FileProviderProfileId("local-test"),
                new FileAuthority("fixture"),
                _root));
        var registration = new FileProviderRegistration(
            "Local files",
            OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
            provider,
            new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root));
        return new FilePanelClient([registration]);
    }

    private static FilePanelLocation Location(string name) =>
        new(
            "local-test",
            "fixture",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments([new FilePanelPathSegment(name)])));

    private static IEnumerable<string> TemporaryCopies()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ghostshell-file-materialized");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : [];
    }

    private static FilePanelClient ClientFor(RemoteBytesProvider provider) =>
        new([
            new FileProviderRegistration(
                "Remote files",
                FileProviderFamily.Sftp,
                provider,
                provider.Root),
        ]);

    /// <summary>
    /// A provider with no local path, whose bounded reads are small enough that
    /// a whole-file copy must loop. Everything a materialization needs and
    /// nothing it does not.
    /// </summary>
    private sealed class RemoteBytesProvider(byte[] content) : IFileProvider
    {
        private static readonly FileAuthority Authority = new("fixture");

        public FileProviderProfileId ProfileId { get; } = new("remote-materialize-test");

        public FileLocation Root => new(ProfileId, Authority, FilePath.Root);

        public FileProviderCapabilities Capabilities { get; } = new(
            FileProviderCapability.Stat | FileProviderCapability.RangedRead,
            FileNameComparison.CaseSensitive,
            new FileProviderLimits(
                maximumListPageSize: 100,
                maximumReadBytes: 1024,
                maximumBufferSize: 1024));

        public ValueTask<FileProviderResult<FilePage>> ListAsync(
            FileListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> StatAsync(
            FileStatRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileProviderResult<FileEntry>.Success(new FileEntry(
                request.Location,
                FileEntryKind.File,
                content.Length,
                LastModifiedAt: null,
                new FileVersion("fixture-version"),
                IsHidden: false)));

        public async ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
            FileReadRequest request,
            Stream destination,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            var offset = checked((int)request.Offset);
            var count = (int)Math.Min(request.MaximumBytes, Math.Max(0, content.Length - offset));
            if (count > 0)
            {
                await destination.WriteAsync(content.AsMemory(offset, count), cancellationToken);
            }

            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                request.Location,
                request.Offset,
                count,
                offset + count < content.Length));
        }

        public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
            FileWriteRequest request,
            Stream source,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
            FileCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
            FileRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
            FileTransferRequest request,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
            FileDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
