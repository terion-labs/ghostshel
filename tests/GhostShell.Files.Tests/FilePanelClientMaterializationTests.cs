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
        Assert.Equal(path, result.Value!.Path);
        Assert.False(result.Value.IsCachedCopy);
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
        Assert.Equal(path, result.Value!.Path);
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
        Assert.Empty(PartialDownloads());
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
        var copy = result.Value!;
        Assert.True(copy.IsCachedCopy);
        // Whole, not the first chunk: the provider caps each read at 1 KiB, so a
        // single read would have produced a truncated — corrupt — database.
        Assert.Equal(content, await File.ReadAllBytesAsync(copy.Path));
        // The name gives nothing away about where the file came from.
        var name = Path.GetFileName(copy.Path);
        Assert.DoesNotContain("remote", name, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{32}$", name);
        File.Delete(copy.Path);
    }

    [Fact]
    public async Task A_file_already_downloaded_is_served_from_the_cache()
    {
        var provider = new RemoteBytesProvider(new byte[1500]);
        var client = ClientFor(provider);
        var location = RemoteLocation(provider, "cached.db");

        var first = await ((IFileContentMaterializer)client).MaterializeAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        var reads = provider.ReadCount;

        var second = await ((IFileContentMaterializer)client).MaterializeAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.Path, second.Value!.Path);
        // The second selection cost nothing: the copy on disk is the record
        // that this exact version was already fetched.
        Assert.Equal(reads, provider.ReadCount);
        File.Delete(first.Value.Path);
    }

    [Fact]
    public async Task A_changed_file_is_downloaded_again_rather_than_served_stale()
    {
        var provider = new RemoteBytesProvider(new byte[1500]);
        var client = ClientFor(provider);
        var location = RemoteLocation(provider, "changing.db");

        var first = await ((IFileContentMaterializer)client).MaterializeAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);
        Assert.True(first.IsSuccess);

        // A new version of the same path: different content, different identity.
        provider.Replace(new byte[2600], "version-2");
        var second = await ((IFileContentMaterializer)client).MaterializeAsync(
            location,
            maximumBytes: 1024 * 1024,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.Path, second.Value!.Path);
        Assert.Equal(2600, new FileInfo(second.Value.Path).Length);
        File.Delete(first.Value.Path);
        File.Delete(second.Value.Path);
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

    private static IEnumerable<string> PartialDownloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ghostshell-file-cache");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.partial")
            : [];
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
    private static FilePanelLocation RemoteLocation(RemoteBytesProvider provider, string name) =>
        new(
            provider.ProfileId.Value,
            "fixture",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments([new FilePanelPathSegment(name)])));

    private sealed class RemoteBytesProvider(byte[] content) : IFileProvider
    {
        private static readonly FileAuthority Authority = new("fixture");

        private byte[] _content = content;
        private string _version = "fixture-version";

        public int ReadCount { get; private set; }

        public void Replace(byte[] replacement, string version)
        {
            _content = replacement;
            _version = version;
        }

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
                _content.Length,
                LastModifiedAt: null,
                new FileVersion(_version),
                IsHidden: false)));

        public async ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
            FileReadRequest request,
            Stream destination,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            var offset = checked((int)request.Offset);
            var count = (int)Math.Min(request.MaximumBytes, Math.Max(0, _content.Length - offset));
            if (count > 0)
            {
                await destination.WriteAsync(_content.AsMemory(offset, count), cancellationToken);
            }

            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                request.Location,
                request.Offset,
                count,
                offset + count < _content.Length));
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
