using GhostShell.Application;
using GhostShell.Core;

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
            using var store = new CefBrowserProfileStore();

            Assert.False(Directory.Exists(root));
            var state = store.ReadState(Selection("profile.one"), expectedRevision: 7);
            Assert.Equal(0, state.ActiveContexts);
            Assert.Equal(0, state.ActiveLeases);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingAnEmptyExactRevisionIsSuccessfulAndIdempotent()
    {
        var root = TemporaryRoot();
        try
        {
            using var store = new CefBrowserProfileStore();
            var request = new BrowserProfileClearRequest(
                Selection("profile.empty"),
                expectedRevision: 11,
                BrowserProfileDataCategory.AllEphemeralWebContent);

            var first = await store.ClearAsync(request, CancellationToken.None);
            var second = await store.ClearAsync(request, CancellationToken.None);

            Assert.Equal(BrowserProfileClearStatus.Cleared, first.Status);
            Assert.Equal(0, first.ClearedBytes);
            Assert.Equal(BrowserProfileClearStatus.Cleared, second.Status);
            Assert.Equal(0, second.ClearedBytes);
            Assert.Contains("exact profile revision", first.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingHonorsCancellationBeforeTouchingAProfile()
    {
        var root = TemporaryRoot();
        try
        {
            using var store = new CefBrowserProfileStore();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await store.ClearAsync(
                new BrowserProfileClearRequest(
                    Selection("profile.cancelled"),
                    expectedRevision: 3,
                    BrowserProfileDataCategory.Cookies),
                cancellation.Token);

            Assert.Equal(BrowserProfileClearStatus.Cancelled, result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocalLeasesShareOneExactContextAndTheFinalLeaseDestroysIt()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.shared", revision: 7);

        var first = store.AcquireLocal(binding);
        var second = store.AcquireLocal(binding);

        var context = Assert.Single(contexts.Created);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 1, 2),
            store.ReadState(binding.Selection, expectedRevision: 7));

        first.Dispose();

        Assert.Equal(0, context.DisposeCount);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 1, 1),
            store.ReadState(binding.Selection, expectedRevision: 7));

        second.Dispose();

        Assert.Equal(1, context.DisposeCount);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 0, 0),
            store.ReadState(binding.Selection, expectedRevision: 7));
    }

    [Fact]
    public void RoutedLeasesShareOnlyTheExactRevisionRouteAndProxyEndpoint()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.routed", revision: 9);

        var first = store.AcquireRouted(binding, "connection.one", 41001);
        var second = store.AcquireRouted(binding, "connection.one", 41001);

        var context = Assert.Single(contexts.Created);
        Assert.NotEmpty(context.Preferences);
        Assert.Throws<InvalidOperationException>(() =>
            store.AcquireRouted(binding, "connection.one", 41002));
        Assert.Single(contexts.Created);

        first.Dispose();
        Assert.Equal(0, context.DisposeCount);
        second.Dispose();
        Assert.Equal(1, context.DisposeCount);
    }

    [Fact]
    public void ProfileRevisionAndRouteEachFormAnIsolationBoundary()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var firstRevision = Binding("profile.isolated", revision: 3);
        var secondRevision = Binding("profile.isolated", revision: 4);
        var otherProfile = Binding("profile.other", revision: 3);

        using var firstLocal = store.AcquireLocal(firstRevision);
        using var secondLocal = store.AcquireLocal(secondRevision);
        using var otherLocal = store.AcquireLocal(otherProfile);
        using var firstRoute = store.AcquireRouted(
            firstRevision,
            "connection.one",
            42001);
        using var secondRoute = store.AcquireRouted(
            firstRevision,
            "connection.two",
            42002);

        Assert.Equal(5, contexts.Created.Count);
        Assert.Equal(3, store.ReadState(
            firstRevision.Selection,
            firstRevision.Revision).ActiveContexts);
        Assert.Equal(1, store.ReadState(
            secondRevision.Selection,
            secondRevision.Revision).ActiveContexts);
        Assert.Equal(1, store.ReadState(
            otherProfile.Selection,
            otherProfile.Revision).ActiveContexts);
    }

    [Fact]
    public async Task ExactCategoryClearsTouchOnlyTheSelectedProfileRevision()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var selected = Binding("profile.selected", revision: 12);
        var other = Binding("profile.other", revision: 12);
        using var selectedLocal = store.AcquireLocal(selected);
        using var selectedRouted = store.AcquireRouted(
            selected,
            "connection.selected",
            43001);
        using var otherLocal = store.AcquireLocal(other);

        var cookies = await store.ClearAsync(
            new BrowserProfileClearRequest(
                selected.Selection,
                selected.Revision,
                BrowserProfileDataCategory.Cookies),
            CancellationToken.None);
        var authentication = await store.ClearAsync(
            new BrowserProfileClearRequest(
                selected.Selection,
                selected.Revision,
                BrowserProfileDataCategory.HttpAuthentication),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.Cleared, cookies.Status);
        Assert.Equal(BrowserProfileClearStatus.Cleared, authentication.Status);
        Assert.All(contexts.Created.Take(2), context =>
        {
            Assert.Equal(1, context.DeleteCookiesCount);
            Assert.Equal(1, context.ClearHttpAuthCredentialsCount);
            Assert.Equal(1, context.CloseAllConnectionsCount);
        });
        Assert.Equal(0, contexts.Created[2].DeleteCookiesCount);
        Assert.Equal(0, contexts.Created[2].ClearHttpAuthCredentialsCount);
        Assert.Equal(0, contexts.Created[2].CloseAllConnectionsCount);
    }

    [Fact]
    public async Task FullResetRefusesAnExactRevisionWhileItHasAnOwner()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.in-use", revision: 5);
        using var lease = store.AcquireLocal(binding);

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                binding.Selection,
                binding.Revision,
                BrowserProfileDataCategory.AllEphemeralWebContent),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.InUse, result.Status);
        Assert.Equal(0, Assert.Single(contexts.Created).DeleteCookiesCount);
    }

    [Fact]
    public async Task ClearFailsClosedWhenAnotherRevisionOwnsThePartition()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var current = Binding("profile.revision", revision: 8);
        using var lease = store.AcquireLocal(current);

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                current.Selection,
                expectedRevision: 7,
                BrowserProfileDataCategory.Cookies),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.RevisionMismatch, result.Status);
        Assert.Equal(0, Assert.Single(contexts.Created).DeleteCookiesCount);
    }

    [Fact]
    public async Task ClearCancellationStopsBeforeTheNextMatchingRoute()
    {
        using var cancellation = new CancellationTokenSource();
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.cancel-between-routes", revision: 6);
        using var local = store.AcquireLocal(binding);
        using var routed = store.AcquireRouted(
            binding,
            "connection.cancelled",
            44001);
        contexts.Created[0].CookiesDeleted = cancellation.Cancel;

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                binding.Selection,
                binding.Revision,
                BrowserProfileDataCategory.Cookies),
            cancellation.Token);

        Assert.Equal(BrowserProfileClearStatus.Cancelled, result.Status);
        Assert.Equal(1, contexts.Created[0].DeleteCookiesCount);
        Assert.Equal(0, contexts.Created[1].DeleteCookiesCount);
    }

    [Fact]
    public void LegacyProfileCleanupRefusesToFollowARootLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = TemporaryRoot();
        var outside = TemporaryRoot();
        var link = Path.Combine(parent, "profiles");
        try
        {
            var marker = Path.Combine(outside, "must-survive");
            File.WriteAllText(marker, "legacy");
            Directory.CreateSymbolicLink(link, outside);

            Assert.Throws<IOException>(() =>
                CefBrowserProfileStore.DeleteOwnedDirectory(link));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(parent, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static BrowserProfileSelection Selection(string id) => new(
        new BrowserProfileId(id),
        BrowserProfileKey.ForNamed(id));

    private static BrowserProfileBinding Binding(string id, long revision)
    {
        var selection = Selection(id);
        return new BrowserProfileBinding(
            selection,
            new BrowserProfileDefinition(
                selection.ProfileId,
                BrowserProfileDefinition.CurrentSchemaVersion,
                id,
                BrowserProfilePersistence.DurableMetadata,
                BrowserProfilePrivacyPolicy.Strict),
            revision);
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

    private sealed class RecordingRequestContextFactory
    {
        public List<RecordingRequestContext> Created { get; } = [];

        public ICefBrowserRequestContext Create()
        {
            var context = new RecordingRequestContext();
            Created.Add(context);
            return context;
        }
    }

    private sealed class RecordingRequestContext : ICefBrowserRequestContext
    {
        public Dictionary<string, string> Preferences { get; } = [];

        public Action? CookiesDeleted { get; set; }

        public int DeleteCookiesCount { get; private set; }

        public int ClearHttpAuthCredentialsCount { get; private set; }

        public int CloseAllConnectionsCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool SetPreference(string name, string value)
        {
            Preferences.Add(name, value);
            return true;
        }

        public void DeleteCookies()
        {
            DeleteCookiesCount++;
            CookiesDeleted?.Invoke();
        }

        public void ClearHttpAuthCredentials() =>
            ClearHttpAuthCredentialsCount++;

        public void CloseAllConnections() => CloseAllConnectionsCount++;

        public CefBrowserView CreateView(
            BrowserProfileBinding profile,
            IBrowserProfileAuthenticationResolver? authenticationResolver) =>
            throw new NotSupportedException(
                "The profile-store tests do not create native browser views.");

        public void Dispose() => DisposeCount++;
    }
}
