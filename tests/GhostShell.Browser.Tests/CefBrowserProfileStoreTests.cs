using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class CefBrowserProfileStoreTests
{
    [Fact]
    public void ConstructionDoesNotCreatePersistentProfileStorage()
    {
        var parent = TemporaryRoot();
        var root = Path.Combine(parent, "profiles");
        try
        {
            using var store = new CefBrowserProfileStore(root);

            Assert.False(Directory.Exists(root));
            Assert.Equal(new BrowserProfileStorageUsage(0, 0, 0), store.ReadUsage());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingWorkspaceProfilesPreservesGlobalAndWebAppData()
    {
        var root = TemporaryRoot();
        try
        {
            Write(root, "global/local/Cookies", "shared");
            Write(root, "workspaces/a/local/Cookies", "workspace");
            Write(root, "webapps/b/local/Cookies", "webapp");
            using var store = new CefBrowserProfileStore(root);

            var before = store.ReadUsage();
            var result = await store.ClearAsync(
                BrowserProfileDataScope.Workspaces,
                CancellationToken.None);

            Assert.Equal(BrowserProfileClearStatus.Cleared, result.Status);
            Assert.Equal("workspace".Length, result.ClearedBytes);
            Assert.Equal("shared".Length, before.GlobalBytes);
            Assert.Equal("workspace".Length, before.WorkspaceBytes);
            Assert.Equal("webapp".Length, before.WebAppBytes);
            Assert.True(File.Exists(Path.Combine(root, "global/local/Cookies")));
            Assert.False(Directory.Exists(Path.Combine(root, "workspaces")));
            Assert.True(File.Exists(Path.Combine(root, "webapps/b/local/Cookies")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClearingAnEmptyProfileIsSuccessfulAndIdempotent()
    {
        var root = TemporaryRoot();
        try
        {
            using var store = new CefBrowserProfileStore(root);

            var first = await store.ClearAsync(
                BrowserProfileDataScope.All,
                CancellationToken.None);
            var second = await store.ClearAsync(
                BrowserProfileDataScope.All,
                CancellationToken.None);

            Assert.Equal(BrowserProfileClearStatus.Cleared, first.Status);
            Assert.Equal(0, first.ClearedBytes);
            Assert.Equal(BrowserProfileClearStatus.Cleared, second.Status);
            Assert.Equal(0, second.ClearedBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClearingFailsClosedAtAnUnexpectedFilesystemLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryRoot();
        var outside = TemporaryRoot();
        try
        {
            var outsideCookie = Path.Combine(outside, "Cookies");
            File.WriteAllText(outsideCookie, "must-survive");
            var workspace = Path.Combine(root, "workspaces", "profile");
            Directory.CreateDirectory(workspace);
            Directory.CreateSymbolicLink(
                Path.Combine(workspace, "escaped"),
                outside);
            using var store = new CefBrowserProfileStore(root);

            var result = await store.ClearAsync(
                BrowserProfileDataScope.Workspaces,
                CancellationToken.None);

            Assert.Equal(BrowserProfileClearStatus.Failed, result.Status);
            Assert.True(File.Exists(outsideCookie));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-browser-profile-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relativePath, string value)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
}
