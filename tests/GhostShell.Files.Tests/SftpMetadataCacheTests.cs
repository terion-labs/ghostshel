namespace GhostShell.Files.Tests;

public sealed class SftpMetadataCacheTests
{
    [Fact]
    public void DirectoryListingsSeedChildMetadataAndExpire()
    {
        var time = new ManualTimeProvider();
        var cache = new SftpMetadataCache(time, TimeSpan.FromSeconds(10), maximumEntries: 8);
        var child = Entry("child");

        cache.StoreDirectory("/", [child]);

        Assert.True(cache.TryGet("/child", out var cached));
        Assert.Same(child, cached);

        time.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryGet("/child", out _));
    }

    [Fact]
    public void NewDirectorySnapshotRemovesChildrenThatNoLongerExist()
    {
        var cache = new SftpMetadataCache(
            TimeProvider.System,
            TimeSpan.FromMinutes(1),
            maximumEntries: 8);

        cache.StoreDirectory("/home", [Entry("old")]);
        cache.StoreDirectory("/home", [Entry("new")]);

        Assert.False(cache.TryGet("/home/old", out _));
        Assert.True(cache.TryGet("/home/new", out _));
    }

    private static RemoteFileEntry Entry(string name) =>
        new(
            name,
            FileEntryKind.Directory,
            null,
            null,
            $"test:{name}");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
