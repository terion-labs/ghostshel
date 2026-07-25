using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed class NativeMacOsArtifactPublisherTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        MacOsPackagePaths.RequireExistingDirectory(
            Path.GetTempPath(),
            "native publisher test temporary directory"),
        $"ghostshell-native-publisher-tests-{Guid.NewGuid():N}");

    public NativeMacOsArtifactPublisherTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Publisher_exclusively_moves_a_staged_first_build()
    {
        var paths = CreatePublicationPaths("first-build");
        WriteMarker(paths.StagedDirectory, "new");

        var result = NativeMacOsArtifactPublisher.Publish(
            paths.StagedDirectory,
            paths.DestinationDirectory);

        Assert.False(result.ReplacedExistingDirectory);
        Assert.Equal(paths.DestinationDirectory, result.DestinationDirectory);
        Assert.False(Directory.Exists(paths.StagedDirectory));
        Assert.Equal("new", ReadMarker(paths.DestinationDirectory));
    }

    [Fact]
    public void Publisher_atomically_exchanges_a_rebuild_with_the_existing_tree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePublicationPaths("rebuild");
        WriteMarker(paths.StagedDirectory, "new");
        WriteMarker(paths.DestinationDirectory, "old");

        var result = NativeMacOsArtifactPublisher.Publish(
            paths.StagedDirectory,
            paths.DestinationDirectory);

        Assert.True(result.ReplacedExistingDirectory);
        Assert.Equal("new", ReadMarker(paths.DestinationDirectory));
        Assert.Equal("old", ReadMarker(paths.StagedDirectory));
    }

    [Fact]
    public void Publisher_preserves_a_real_destination_when_staged_validation_fails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePublicationPaths("source-failure");
        WriteMarker(paths.StagedDirectory, "new");
        WriteMarker(paths.DestinationDirectory, "old");
        File.CreateSymbolicLink(
            Path.Combine(paths.StagedDirectory, "unsafe-link"),
            Path.Combine(paths.StagedDirectory, "marker.txt"));

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsArtifactPublisher.Publish(
                paths.StagedDirectory,
                paths.DestinationDirectory));

        Assert.Equal("old", ReadMarker(paths.DestinationDirectory));
        Assert.Equal("new", ReadMarker(paths.StagedDirectory));
    }

    [Fact]
    public void Publisher_rejects_a_preexisting_destination_symlink_without_following_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePublicationPaths("target-link");
        WriteMarker(paths.StagedDirectory, "new");
        var outside = Path.Combine(_temporaryDirectory, "outside-target");
        WriteMarker(outside, "outside");
        Directory.CreateSymbolicLink(paths.DestinationDirectory, outside);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsArtifactPublisher.Publish(
                paths.StagedDirectory,
                paths.DestinationDirectory));

        Assert.Equal("outside", ReadMarker(outside));
        Assert.Equal("new", ReadMarker(paths.StagedDirectory));
        Assert.Equal(outside, new DirectoryInfo(paths.DestinationDirectory).LinkTarget);
    }

    [Fact]
    public void Publisher_rejects_links_nested_in_an_existing_destination()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePublicationPaths("nested-target-link");
        WriteMarker(paths.StagedDirectory, "new");
        WriteMarker(paths.DestinationDirectory, "old");
        File.CreateSymbolicLink(
            Path.Combine(paths.DestinationDirectory, "unsafe-link"),
            Path.Combine(paths.DestinationDirectory, "marker.txt"));

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsArtifactPublisher.Publish(
                paths.StagedDirectory,
                paths.DestinationDirectory));

        Assert.Equal("old", ReadMarker(paths.DestinationDirectory));
        Assert.Equal("new", ReadMarker(paths.StagedDirectory));
    }

    [Fact]
    public void Publisher_rejects_a_symlink_in_the_staged_and_destination_ancestry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var realOwner = Path.Combine(_temporaryDirectory, "real-owner");
        var aliasOwner = Path.Combine(_temporaryDirectory, "owner-alias");
        Directory.CreateDirectory(realOwner);
        Directory.CreateSymbolicLink(aliasOwner, realOwner);
        var privateParent = Path.Combine(
            aliasOwner,
            $".ghostshell-native-artifacts.{Guid.NewGuid():N}");
        var staged = Path.Combine(privateParent, "osx-arm64");
        var destination = Path.Combine(aliasOwner, "osx-arm64");
        WriteMarker(staged, "new");

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsArtifactPublisher.Publish(staged, destination));

        Assert.False(Directory.Exists(destination));
        Assert.Equal("new", ReadMarker(staged));
    }

    [Fact]
    public void Publish_command_requires_exactly_the_staged_and_destination_options()
    {
        var command = NativeMacOsArtifactPublishCommand.Parse(
        [
            "--staged-directory",
            "/private/osx-arm64",
            "--destination",
            "/artifacts/osx-arm64",
        ]);

        Assert.Equal("/private/osx-arm64", command.StagedDirectory);
        Assert.Equal("/artifacts/osx-arm64", command.DestinationDirectory);
        Assert.Throws<PackagingUsageException>(() =>
            NativeMacOsArtifactPublishCommand.Parse(
            [
                "--staged-directory",
                "/private/osx-arm64",
            ]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private PublicationPaths CreatePublicationPaths(string name)
    {
        var owner = Path.Combine(_temporaryDirectory, name);
        Directory.CreateDirectory(owner);
        var privateParent = Path.Combine(
            owner,
            $".ghostshell-native-artifacts.{Guid.NewGuid():N}");
        var staged = Path.Combine(privateParent, "osx-arm64");
        var destination = Path.Combine(owner, "osx-arm64");
        Directory.CreateDirectory(staged);
        return new PublicationPaths(staged, destination);
    }

    private static void WriteMarker(string directory, string value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "marker.txt"), value);
    }

    private static string ReadMarker(string directory) =>
        File.ReadAllText(Path.Combine(directory, "marker.txt"));

    private sealed record PublicationPaths(
        string StagedDirectory,
        string DestinationDirectory);
}
