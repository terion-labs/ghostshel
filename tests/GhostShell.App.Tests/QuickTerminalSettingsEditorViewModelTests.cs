using GhostShell.Application;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class QuickTerminalSettingsEditorViewModelTests
{
    [Fact]
    public void Save_request_parses_hotkey_and_percent_fields()
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 7)
        {
            HotkeyText = "Control + Option + K",
            MonitorPolicy = QuickTerminalMonitorPolicy.Primary,
            HeightPercent = 40,
            OpacityPercent = 70,
            BlurRadius = 0,
            AnimateSlide = false,
            AnimationDurationMilliseconds = 90,
            ReduceMotion = true,
            RestoreLastSession = false,
            HideOnFocusLoss = false,
        };

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(
            new KeyStroke("K", KeyModifiers.Control | KeyModifiers.Alt),
            request.Settings.Hotkey);
        Assert.Equal(QuickTerminalMonitorPolicy.Primary, request.Settings.MonitorPolicy);
        Assert.Equal(0.4, request.Settings.HeightFraction);
        Assert.Equal(0.7, request.Settings.Opacity);
        Assert.False(request.Settings.RestoreLastSession);
        Assert.False(request.Settings.HideOnFocusLoss);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GRAVE")]
    [InlineData("Command + K + L")]
    public void Invalid_hotkey_text_is_rejected_before_persistence(string text)
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1)
        {
            HotkeyText = text,
        };

        var exception = Record.Exception(() => editor.CreateSaveRequest());
        Assert.True(exception is ArgumentException or FormatException);
    }

    [Fact]
    public void Conflict_status_reports_the_active_fallback()
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1);
        var configured = new KeyStroke("K", KeyModifiers.Meta);
        var fallback = QuickTerminalSettings.Default.Hotkey;
        var failure = new GlobalHotkeyRegistrationResult.Failure(new(
            GlobalHotkeyRegistrationErrorCode.Conflict,
            "global_hotkey_conflict",
            "Another application owns the shortcut."));

        editor.ApplyRegistration(configured, fallback, failure);

        Assert.Contains("Another application", editor.RegistrationStatus);
        Assert.Contains(
            $"{QuickTerminalHotkeyText.Example} remains active",
            editor.RegistrationStatus);
        Assert.Equal("#FFB224", editor.RegistrationStatusBrush);
    }

    [Theory]
    [InlineData((int)ShortcutDisplayPlatform.MacOS, "Control + Option + Shift + Command + K")]
    [InlineData((int)ShortcutDisplayPlatform.Windows, "Ctrl + Alt + Shift + Win + K")]
    [InlineData((int)ShortcutDisplayPlatform.Linux, "Ctrl + Alt + Shift + Super + K")]
    public void Shortcut_format_uses_host_conventions(
        int platform,
        string expected)
    {
        var stroke = new KeyStroke(
            "K",
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);

        Assert.Equal(
            expected,
            QuickTerminalHotkeyText.Format(stroke, (ShortcutDisplayPlatform)platform));
    }

    [Theory]
    [InlineData((int)ShortcutDisplayPlatform.MacOS, "⌘ K")]
    [InlineData((int)ShortcutDisplayPlatform.Windows, "Ctrl+K")]
    [InlineData((int)ShortcutDisplayPlatform.Linux, "Ctrl+K")]
    public void Application_shortcut_format_uses_host_conventions(
        int platform,
        string expected)
    {
        Assert.Equal(
            expected,
            QuickTerminalHotkeyText.FormatApplicationCommand(
                "K",
                (ShortcutDisplayPlatform)platform));
    }
}
