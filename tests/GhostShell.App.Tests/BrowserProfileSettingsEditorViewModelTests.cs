using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

public sealed class BrowserProfileSettingsEditorViewModelTests
{
    [Fact]
    public void SharingChoiceAppliesImmediatelyToNewBrowserProfiles()
    {
        var preferences = new InMemoryBrowserProfilePreferences();
        var editor = new BrowserProfileSettingsEditorViewModel(preferences);

        editor.SelectedSharing = Assert.Single(
            editor.SharingOptions,
            option => option.Sharing == BrowserProfileSharing.PerWorkspace);

        Assert.Equal(BrowserProfileSharing.PerWorkspace, preferences.Current.Sharing);
    }

    [Fact]
    public async Task ClearCommandUsesTheSelectedScopeAndRefreshesUsage()
    {
        var data = new RecordingBrowserProfileDataControl();
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            data);

        editor.ClearWorkspacesCommand.Execute(null);
        await data.Cleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => editor.OperationStatus is not null);
        await WaitForAsync(() => editor.UsageText.StartsWith(
            "Shared ",
            StringComparison.Ordinal));

        Assert.Equal(BrowserProfileDataScope.Workspaces, data.LastScope);
        Assert.Equal("Workspace browser data was cleared.", editor.OperationStatus);
        Assert.Equal("Shared 1 KB · workspaces 2 KB · WebApps 3 KB", editor.UsageText);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "The settings command did not finish.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingBrowserProfileDataControl :
        IBrowserProfileDataControl
    {
        public TaskCompletionSource Cleared { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserProfileDataScope? LastScope { get; private set; }

        public BrowserProfileStorageUsage ReadUsage() => new(1024, 2048, 3072);

        public ValueTask<BrowserProfileClearResult> ClearAsync(
            BrowserProfileDataScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScope = scope;
            Cleared.TrySetResult();
            return ValueTask.FromResult(new BrowserProfileClearResult(
                BrowserProfileClearStatus.Cleared,
                2048,
                "Workspace browser data was cleared."));
        }
    }
}
