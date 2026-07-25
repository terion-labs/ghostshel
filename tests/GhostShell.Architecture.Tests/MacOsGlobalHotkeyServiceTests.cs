using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

[Collection("native global hotkey")]
public sealed class MacOsGlobalHotkeyServiceTests
{
    private static readonly KeyStroke IsolatedGesture = new(
        "GRAVE",
        KeyModifiers.Control
        | KeyModifiers.Alt
        | KeyModifiers.Shift
        | KeyModifiers.Meta);

    [Fact]
    public void Duplicate_primary_registration_reports_conflict_and_release_allows_retry()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var first = new MacOsGlobalHotkeyService();
        using var second = new MacOsGlobalHotkeyService();

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(first.Register(IsolatedGesture));
        var conflict = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            second.Register(IsolatedGesture));
        Assert.Equal(GlobalHotkeyRegistrationErrorCode.Conflict, conflict.Error.Code);

        first.Unregister();
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(second.Register(IsolatedGesture));
    }

}
