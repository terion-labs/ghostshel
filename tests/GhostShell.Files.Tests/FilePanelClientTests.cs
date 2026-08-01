using System.Text;
using GhostShell.Application;

namespace GhostShell.Files.Tests;

public sealed class FilePanelClientTests
{
    [Fact]
    public void ProfilesExposeStructuredRootAndMappedCapabilities()
    {
        using var root = TemporaryDirectory.Create();
        var (client, provider) = CreateClient(root.Path);

        var profile = Assert.Single(client.Profiles);

        Assert.Equal(provider.ProfileId.Value, profile.Id);
        Assert.Equal("Local files", profile.Name);
        Assert.Equal(
            OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
            profile.Family);
        var rootAddress = Assert.IsType<FilePanelAddress.Hierarchical>(profile.Root.Address);
        Assert.True(rootAddress.Path.IsRoot);
        Assert.True(profile.Capabilities.HasFlag(FilePanelCapability.List));
        Assert.True(profile.Capabilities.HasFlag(FilePanelCapability.RangedRead));
        Assert.InRange(profile.MaximumPreviewBytes, 1, 1024 * 1024);
    }

    [Fact]
    public void RegistrationDefaultsToNoGovernedMutationsAndRejectsUnsupportedClaims()
    {
        var provider = new PreviewReceiptProvider((request, _) =>
            new FileReadReceipt(
                request.Location,
                request.Offset,
                BytesRead: 0,
                IsTruncated: false));
        var registration = new FileProviderRegistration(
            "Read only",
            FileProviderFamily.WebDav,
            provider,
            provider.Root);

        Assert.Equal(
            FilePanelCapability.None,
            registration.GovernedMutationCapabilities);
        Assert.Contains(
            typeof(FileProviderRegistration).GetConstructors(),
            constructor => constructor.GetParameters().Length == 4);
        Assert.Throws<ArgumentException>(() =>
            new FileProviderRegistration(
                "Invalid mkdir claim",
                FileProviderFamily.WebDav,
                provider,
                provider.Root,
                FilePanelCapability.GovernedCreateDirectory));
        Assert.Throws<ArgumentException>(() =>
            new FileProviderRegistration(
                "Invalid delete claim",
                FileProviderFamily.S3,
                provider,
                provider.Root,
                FilePanelCapability.GovernedDelete));
    }

    [Fact]
    public async Task ListMapsLocationsAndHonorsHiddenFilter()
    {
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "visible.txt"), "visible");
        var hiddenPath = Path.Combine(root.Path, OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden");
        await File.WriteAllTextAsync(hiddenPath, "hidden");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
        }

        var (client, _) = CreateClient(root.Path);
        var profile = Assert.Single(client.Profiles);

        var hiddenExcluded = await client.ListAsync(
            new FilePanelListRequest(profile.Root, 100, null, ShowHidden: false),
            CancellationToken.None);
        var hiddenIncluded = await client.ListAsync(
            new FilePanelListRequest(profile.Root, 100, null, ShowHidden: true),
            CancellationToken.None);

        Assert.True(hiddenExcluded.IsSuccess, hiddenExcluded.Error?.Message);
        Assert.Single(hiddenExcluded.Value!.Entries, item => item.Name == "visible.txt");
        Assert.DoesNotContain(hiddenExcluded.Value.Entries, item => item.IsHidden);
        Assert.True(hiddenIncluded.IsSuccess, hiddenIncluded.Error?.Message);
        Assert.Contains(hiddenIncluded.Value!.Entries, item => item.IsHidden);
        var visible = Assert.Single(hiddenIncluded.Value.Entries, item => item.Name == "visible.txt");
        var address = Assert.IsType<FilePanelAddress.Hierarchical>(visible.Location.Address);
        Assert.Equal("visible.txt", address.Path.Name?.Value);
    }

    [Fact]
    public async Task PreviewClassifiesJsonAndBinaryWithoutExceedingCallerBound()
    {
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "settings.json"), "{\"enabled\":true}");
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "payload.bin"), [0x00, 0xFF, 0x10, 0x20]);
        var (client, _) = CreateClient(root.Path);
        var profile = Assert.Single(client.Profiles);

        var json = await client.PreviewAsync(
            new FilePanelPreviewRequest(Child(profile.Root, "settings.json"), 128),
            CancellationToken.None);
        var binary = await client.PreviewAsync(
            new FilePanelPreviewRequest(Child(profile.Root, "payload.bin"), 2),
            CancellationToken.None);

        Assert.True(json.IsSuccess, json.Error?.Message);
        Assert.Equal(FilePanelPreviewKind.StructuredText, json.Value!.Kind);
        Assert.Equal("application/json", json.Value.MediaType);
        Assert.Equal("{\"enabled\":true}", Encoding.UTF8.GetString(json.Value.Content.Span));
        Assert.True(binary.IsSuccess, binary.Error?.Message);
        Assert.Equal(FilePanelPreviewKind.Hex, binary.Value!.Kind);
        Assert.Equal(2, binary.Value.Content.Length);
        Assert.True(binary.Value.IsTruncated);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("authority")]
    [InlineData("address")]
    public async Task PreviewRejectsReceiptFromAnotherSource(string mismatch)
    {
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write("safe"u8);
            var source = mismatch switch
            {
                "profile" => new FileLocation(
                    new FileProviderProfileId("other-profile"),
                    request.Location.Authority,
                    request.Location.Path),
                "authority" => new FileLocation(
                    request.Location.ProviderProfileId,
                    new FileAuthority("other-authority"),
                    request.Location.Path),
                "address" => request.Location.Child(new FilePathSegment("other.txt")),
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, null),
            };
            return new FileReadReceipt(
                source,
                request.Offset,
                BytesRead: 4,
                IsTruncated: false);
        });
        var (client, location) = CreatePreviewClient(provider);

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 16),
            CancellationToken.None);

        AssertInvalidProviderReceipt(result);
    }

    [Fact]
    public async Task PreviewRejectsReceiptWithDifferentRequestedVersion()
    {
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write("safe"u8);
            return new FileReadReceipt(
                request.Location.WithVersion(new FileVersion("different-version")),
                request.Offset,
                BytesRead: 4,
                IsTruncated: false);
        });
        var (client, unversionedLocation) = CreatePreviewClient(provider);
        var location = unversionedLocation.WithVersion("requested-version");

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 16),
            CancellationToken.None);

        AssertInvalidProviderReceipt(result);
    }

    [Fact]
    public async Task PreviewRejectsReceiptWithUnexpectedOffset()
    {
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write("safe"u8);
            return new FileReadReceipt(
                request.Location,
                Offset: 1,
                BytesRead: 4,
                IsTruncated: false);
        });
        var (client, location) = CreatePreviewClient(provider);

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 16),
            CancellationToken.None);

        AssertInvalidProviderReceipt(result);
    }

    [Fact]
    public async Task PreviewRejectsReceiptWhoseByteCountDiffersFromDestination()
    {
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write("safe"u8);
            return new FileReadReceipt(
                request.Location,
                request.Offset,
                BytesRead: 3,
                IsTruncated: false);
        });
        var (client, location) = CreatePreviewClient(provider);

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 16),
            CancellationToken.None);

        AssertInvalidProviderReceipt(result);
    }

    [Fact]
    public async Task PreviewDestinationCannotGrowPastCallerBound()
    {
        var overflowRejected = false;
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write(new byte[checked((int)request.MaximumBytes)]);
            try
            {
                destination.WriteByte(0);
            }
            catch (NotSupportedException)
            {
                overflowRejected = true;
            }

            return new FileReadReceipt(
                request.Location,
                request.Offset,
                request.MaximumBytes + 1,
                IsTruncated: false);
        });
        var (client, location) = CreatePreviewClient(provider);

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 4),
            CancellationToken.None);

        Assert.True(overflowRejected);
        AssertInvalidProviderReceipt(result);
    }

    [Fact]
    public async Task PreviewPreservesProviderValidatedSourceVersion()
    {
        var provider = new PreviewReceiptProvider((request, destination) =>
        {
            destination.Write("{\"safe\":true}"u8);
            return new FileReadReceipt(
                request.Location.WithVersion(new FileVersion("validated-version")),
                request.Offset,
                BytesRead: destination.Length,
                IsTruncated: false);
        });
        var (client, location) = CreatePreviewClient(provider, "settings.json");

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(location, 64),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("validated-version", result.Value!.Location.Version);
        Assert.Equal(FilePanelPreviewKind.StructuredText, result.Value.Kind);
    }

    [Fact]
    public async Task MutationsPreservePreconditionsAndReturnTypedResults()
    {
        using var root = TemporaryDirectory.Create();
        var (client, _) = CreateClient(root.Path);
        var profile = Assert.Single(client.Profiles);
        var original = Child(profile.Root, "original");
        var renamed = Child(profile.Root, "renamed");

        var created = await client.CreateDirectoryAsync(
            new FilePanelCreateDirectoryRequest(
                original,
                FilePanelMutationPrecondition.MustNotExist),
            CancellationToken.None);
        var moved = await client.RenameAsync(
            new FilePanelRenameRequest(
                original,
                renamed,
                FilePanelMutationPrecondition.MustNotExist),
            CancellationToken.None);
        var deleted = await client.DeleteAsync(
            new FilePanelDeleteRequest(
                renamed,
                Recursive: false,
                FilePanelMutationPrecondition.MustExist),
            CancellationToken.None);

        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.True(moved.IsSuccess, moved.Error?.Message);
        Assert.Equal("renamed", moved.Value!.Name);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.True(deleted.Value!.WasDirectory);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "renamed")));
    }

    [Fact]
    public async Task UnknownProfileReturnsStableTypedError()
    {
        using var root = TemporaryDirectory.Create();
        var (client, _) = CreateClient(root.Path);
        var unknown = new FilePanelLocation(
            "missing-profile",
            "fixture",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));

        var result = await client.StatAsync(unknown, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.UnknownProfile, result.Error!.Code);
        Assert.Equal("file_provider_profile_unknown", result.Error.StableCode);
    }

    private static (FilePanelClient Client, LocalFileProvider Provider) CreateClient(string rootPath)
    {
        var options = new LocalFileProviderOptions(
            new FileProviderProfileId("local-panel-test"),
            new FileAuthority("fixture"),
            rootPath);
        var provider = LocalFileProvider.CreateForCurrentPlatform(options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var registration = new FileProviderRegistration(
            "Local files",
            OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
            provider,
            root);
        return (new FilePanelClient([registration]), provider);
    }

    private static (FilePanelClient Client, FilePanelLocation Location) CreatePreviewClient(
        PreviewReceiptProvider provider,
        string name = "preview.txt")
    {
        var registration = new FileProviderRegistration(
            "Adversarial preview",
            FileProviderFamily.Posix,
            provider,
            provider.Root);
        var client = new FilePanelClient([registration]);
        var root = Assert.Single(client.Profiles).Root;
        return (client, Child(root, name));
    }

    private static void AssertInvalidProviderReceipt(
        FilePanelResult<FilePanelPreview> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal("file_provider_receipt_invalid", result.Error.StableCode);
        Assert.False(result.Error.Retryable);
    }

    private static FilePanelLocation Child(FilePanelLocation parent, string name) =>
        parent.Child(new FilePanelPathSegment(name));

    private sealed class PreviewReceiptProvider(
        Func<FileReadRequest, Stream, FileReadReceipt> read) : IFileProvider
    {
        private static readonly FileAuthority Authority = new("fixture");

        public FileProviderProfileId ProfileId { get; } = new("preview-receipt-test");

        public FileLocation Root => new(ProfileId, Authority, FilePath.Root);

        public FileProviderCapabilities Capabilities { get; } = new(
            FileProviderCapability.RangedRead,
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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
            FileReadRequest request,
            Stream destination,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileProviderResult<FileReadReceipt>.Success(read(request, destination)));

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
