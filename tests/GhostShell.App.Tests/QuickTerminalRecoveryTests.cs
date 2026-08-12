using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

public sealed class QuickTerminalRecoveryTests
{
    [Fact]
    public void Recovery_payload_preserves_tab_order_and_active_tab()
    {
        var snapshot = new RuntimeRecoverySnapshot(
            "previous-run",
            QuickTerminalRecoveryCodec.SnapshotKey,
            QuickTerminalRecoveryCodec.SchemaVersion,
            """{"connectionIds":["local","remote",null],"activeTabIndex":1}""",
            DateTimeOffset.UtcNow);

        Assert.True(QuickTerminalRecoveryCodec.TryDeserialize(snapshot, out var payload));
        Assert.Equal(new string?[] { "local", "remote", null }, payload!.ConnectionIds);
        Assert.Equal(1, payload.ActiveTabIndex);
    }

    [Theory]
    [InlineData("""{"connectionIds":[],"activeTabIndex":0}""")]
    [InlineData("""{"connectionIds":["local"],"activeTabIndex":1}""")]
    [InlineData("""{"connectionIds":["bad\u0000id"],"activeTabIndex":0}""")]
    public void Invalid_recovery_payload_is_rejected(string json)
    {
        var snapshot = new RuntimeRecoverySnapshot(
            "previous-run",
            QuickTerminalRecoveryCodec.SnapshotKey,
            QuickTerminalRecoveryCodec.SchemaVersion,
            json,
            DateTimeOffset.UtcNow);

        Assert.False(QuickTerminalRecoveryCodec.TryDeserialize(snapshot, out _));
    }
}
