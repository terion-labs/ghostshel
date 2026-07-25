using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class QuickTerminalDefinitionTrackerTests
{
    [Fact]
    public void TracksSelectedConnectionAndTerminalProfileRevisions()
    {
        var initial = Snapshot(Connection("builtin.local", "Built in"), 2, Profile("builtin.terminal.default"), 4);
        var tracker = new QuickTerminalDefinitionTracker(initial);

        Assert.False(tracker.Update(initial));
        Assert.True(tracker.Update(Snapshot(Connection("builtin.local", "Renamed"), 3, Profile("builtin.terminal.default"), 4)));
        Assert.True(tracker.Update(Snapshot(Connection("builtin.local", "Renamed"), 3, Profile("builtin.terminal.default"), 5)));
    }

    [Fact]
    public void AddingOrRemovingThePreferredLocalConnectionChangesTheRuntimeSelection()
    {
        var custom = Store(Connection("custom", "Custom"), 1);
        var profile = Store(Profile("custom-profile"), 1);
        var tracker = new QuickTerminalDefinitionTracker(new DefinitionCatalogSnapshot(
            [custom], [], [], [], [], [profile], [], [], []));
        var withBuiltIn = new DefinitionCatalogSnapshot(
            [custom, Store(Connection("builtin.local", "Built in"), 1)],
            [], [], [], [], [profile], [], [], []);

        Assert.True(tracker.Update(withBuiltIn));
        Assert.True(tracker.Update(new DefinitionCatalogSnapshot([], [], [], [], [], [profile], [], [], [])));
    }

    [Fact]
    public void UnselectedDefinitionChangesDoNotDiscardTheQuickSession()
    {
        var selected = Store(Connection("builtin.local", "Built in"), 7);
        var profile = Store(Profile("builtin.terminal.default"), 3);
        var initial = new DefinitionCatalogSnapshot(
            [selected, Store(Connection("other", "Other"), 1)],
            [], [], [], [], [profile, Store(Profile("other-profile"), 1)], [], [], []);
        var tracker = new QuickTerminalDefinitionTracker(initial);
        var changedUnselected = new DefinitionCatalogSnapshot(
            [selected, Store(Connection("other", "Other renamed"), 2)],
            [], [], [], [], [profile, Store(Profile("other-profile"), 2)], [], [], []);

        Assert.False(tracker.Update(changedUnselected));
    }

    [Fact]
    public void SelectedTerminalKeymapRevisionChangesDiscardTheQuickSessionSnapshot()
    {
        var keymapId = new KeymapProfileId("quick.custom-terminal-map");
        var connection = Store(Connection("builtin.local", "Built in"), 1);
        var profile = Store(Profile("builtin.terminal.default", keymapId), 1);
        var keymap = new KeymapProfile(keymapId, "Quick map", KeymapLayer.Terminal, []);
        var initial = new DefinitionCatalogSnapshot(
            [connection], [], [], [], [], [profile], [Store(keymap, 4)], [], []);
        var tracker = new QuickTerminalDefinitionTracker(initial);

        var changed = initial with { Keymaps = [Store(keymap, 5)] };

        Assert.True(tracker.Update(changed));
        Assert.Equal(keymapId, QuickTerminalDefinitionSelection.Resolve(changed).TerminalKeymap!.Value.Id);
    }

    private static DefinitionCatalogSnapshot Snapshot(
        ConnectionProfile connection,
        long connectionRevision,
        TerminalProfile profile,
        long profileRevision) => new(
            [Store(connection, connectionRevision)],
            [], [], [], [], [Store(profile, profileRevision)], [], [], []);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition => new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static ConnectionProfile Connection(string id, string name) => new(
        new ConnectionId(id),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable,
        []);

    private static TerminalProfile Profile(string id, KeymapProfileId? keymapId = null) => new(
        new TerminalProfileId(id),
        id,
        "JetBrains Mono",
        14,
        1.2,
        TerminalCursorStyle.Block,
        cursorBlink: true,
        10_000,
        TerminalPalette.GhostShellDark,
        keymapId ?? BuiltInKeymaps.LinuxTerminalId);
}
