using System.Runtime.InteropServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal.Tests;

public sealed class GhosttyNativeLaunchOptionsTests
{
    [Fact]
    public void Versioned_options_preserve_structured_argv_environment_and_profile()
    {
        var launch = new TerminalLaunchRequest(
            "/tmp/work space",
            "/bin/sh",
            ["", "space value", "quote'value", "$(touch /tmp/not-executed)"],
            new Dictionary<string, string>
            {
                ["SECOND"] = "two",
                ["FIRST"] = "one with spaces",
            },
            new TerminalRenderProfileSnapshot(
                14.5,
                TerminalCursorStyle.Bar,
                cursorBlink: true,
                100_000,
                TerminalPalette.GhostShellDark,
                fontFamily: "JetBrains Mono",
                lineHeight: 1.25,
                clipboardPolicy: new TerminalClipboardPolicy(
                    TerminalClipboardAccess.Deny,
                    TerminalClipboardAccess.Ask,
                    TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed),
                linkPolicy: TerminalLinkPolicy.Disabled,
                imeEnabled: false,
                shellIntegration: TerminalShellIntegrationMode.Fish,
                bellMode: TerminalBellMode.SystemAndVisual,
                compatibility: TerminalCompatibilityProfile.Legacy),
            TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.MacOsTerminal));

        using var native = GhosttyNativeLaunchOptions.Create(launch);
        var options = native.Value;

        Assert.Equal(GhosttyNativeLaunchOptions.OptionsVersion, options.Version);
        Assert.Equal((uint)Marshal.SizeOf<NativeTerminalOptionsV1>(), options.StructSize);
        Assert.Equal("/tmp/work space", Marshal.PtrToStringUTF8(options.WorkingDirectory));
        Assert.Equal("/bin/sh", Marshal.PtrToStringUTF8(options.Executable));
        Assert.Equal((nuint)4, options.ArgumentCount);
        Assert.Equal(
            ["", "space value", "quote'value", "$(touch /tmp/not-executed)"],
            ReadPointerArray(options.Arguments, options.ArgumentCount));

        Assert.Equal((nuint)2, options.EnvironmentCount);
        Assert.Equal(
            [new KeyValuePair<string, string>("FIRST", "one with spaces"),
             new KeyValuePair<string, string>("SECOND", "two")],
            ReadEnvironment(options.Environment, options.EnvironmentCount));
        Assert.Equal(1U, options.TerminalKeymapPresent);
        Assert.Equal(
            [
                "super+c=copy_to_clipboard",
                "super+v=paste_from_clipboard",
                "super+a=select_all",
                "alt+left=text:\\x1bb",
                "alt+right=text:\\x1bf",
                "alt+backspace=text:\\x17",
                "alt+delete=text:\\x1bd",
                "super+left=text:\\x01",
                "super+right=text:\\x05",
                "super+f=start_search",
                "super+plus=increase_font_size:1",
                "super+-=decrease_font_size:1",
                "super+0=reset_font_size",
                "shift+super+k=clear_screen",
                "ctrl+c=text:\\x03",
                "ctrl+d=text:\\x04",
                "ctrl+l=text:\\x0c",
            ],
            ReadPointerArray(options.TerminalKeybindings, options.TerminalKeybindingCount));

        var profile = Marshal.PtrToStructure<NativeTerminalRenderProfileV1>(options.RenderProfile);
        Assert.Equal((uint)Marshal.SizeOf<NativeTerminalRenderProfileV1>(), profile.StructSize);
        Assert.Equal(14.5F, profile.FontSize);
        Assert.Equal(1U, profile.CursorStyle);
        Assert.Equal(1U, profile.CursorBlink);
        Assert.Equal(
            100_000UL * GhosttyNativeLaunchOptions.EstimatedBytesPerScrollbackLine,
            profile.ScrollbackLimitBytes);
        Assert.Equal(0x00E8E4DEU, profile.ForegroundRgb);
        Assert.Equal(0x0012100EU, profile.BackgroundRgb);
        Assert.Equal((nuint)16, profile.AnsiPaletteCount);
        Assert.Equal("JetBrains Mono", Marshal.PtrToStringUTF8(profile.FontFamily));
        Assert.Equal(1.25, profile.LineHeight);
        Assert.Equal(2U, profile.ClipboardRead);
        Assert.Equal(0U, profile.ClipboardWrite);
        Assert.Equal(1U, profile.PasteSafety);
        Assert.Equal(2U, profile.LinkPolicy);
        Assert.Equal(0U, profile.ImeEnabled);
        Assert.Equal(4U, profile.ShellIntegration);
        Assert.Equal(2U, profile.BellMode);
        Assert.Equal(2U, profile.Compatibility);
    }

    [Fact]
    public void Native_keymap_rejects_terminal_sequences_and_safely_ignores_unknown_actions()
    {
        var sequence = new TerminalKeymapSnapshot(
            new KeymapProfileId("terminal.sequence"),
            "Sequence",
            [
                new CommandBinding(
                    BuiltInCommands.Copy,
                    KeySequence.Of(
                        new KeyStroke("B", KeyModifiers.Control),
                        new KeyStroke("+", KeyModifiers.Meta)),
                    CommandContext.Terminal),
            ]);

        var error = Assert.Throws<NotSupportedException>(() =>
            GhosttyTerminalKeymap.CreateBindings(sequence));
        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);

        var unsupported = new TerminalKeymapSnapshot(
            new KeymapProfileId("terminal.unsupported"),
            "Unsupported",
            [
                new CommandBinding(
                    BuiltInCommands.NewTab,
                    KeySequence.Of(new KeyStroke("T", KeyModifiers.Meta)),
                    CommandContext.Terminal),
            ]);
        Assert.Equal(
            ["super+t=ignore"],
            GhosttyTerminalKeymap.CreateBindings(unsupported));
    }

    [Fact]
    public void Native_keymap_translates_recorder_oem_names_without_bricking_attachment()
    {
        var keymap = new TerminalKeymapSnapshot(
            new KeymapProfileId("terminal.oem"),
            "OEM keys",
            [
                Binding(BuiltInCommands.Find, "OemPeriod"),
                Binding(BuiltInCommands.Copy, "OemCloseBrackets"),
                Binding(BuiltInCommands.Paste, "OemTilde"),
            ]);

        Assert.Equal(
            [
                "super+Period=start_search",
                "super+BracketRight=copy_to_clipboard",
                "super+Backquote=paste_from_clipboard",
            ],
            GhosttyTerminalKeymap.CreateBindings(keymap));
    }

    [Fact]
    public void Snapshot_carries_every_durable_terminal_setting()
    {
        var profile = new TerminalProfile(
            new TerminalProfileId("full"),
            "Full",
            "Iosevka",
            16,
            1.3,
            TerminalCursorStyle.Underline,
            cursorBlink: true,
            250_000,
            TerminalPalette.GhostShellDark,
            BuiltInKeymaps.MacOsTerminalId,
            new TerminalClipboardPolicy(
                TerminalClipboardAccess.Allow,
                TerminalClipboardAccess.Deny,
                TerminalPasteSafetyPolicy.AllowUnsafe),
            TerminalLinkPolicy.Open,
            imeEnabled: false,
            TerminalShellIntegrationMode.Zsh,
            TerminalBellMode.System,
            TerminalCompatibilityProfile.Xterm256Color);

        var snapshot = TerminalRenderProfileSnapshot.FromProfile(profile);

        Assert.Equal(profile.FontFamily, snapshot.FontFamily);
        Assert.Equal(profile.FontSize, snapshot.FontSize);
        Assert.Equal(profile.LineHeight, snapshot.LineHeight);
        Assert.Equal(profile.CursorStyle, snapshot.CursorStyle);
        Assert.Equal(profile.CursorBlink, snapshot.CursorBlink);
        Assert.Equal(profile.ScrollbackLines, snapshot.ScrollbackLines);
        Assert.Equal(profile.Palette.Name, snapshot.Palette.Name);
        Assert.Equal(profile.Palette.Foreground, snapshot.Palette.Foreground);
        Assert.Equal(profile.Palette.AnsiColors, snapshot.Palette.AnsiColors);
        Assert.Equal(profile.ClipboardPolicy, snapshot.ClipboardPolicy);
        Assert.Equal(profile.LinkPolicy, snapshot.LinkPolicy);
        Assert.Equal(profile.ImeEnabled, snapshot.ImeEnabled);
        Assert.Equal(profile.ShellIntegration, snapshot.ShellIntegration);
        Assert.Equal(profile.BellMode, snapshot.BellMode);
        Assert.Equal(profile.Compatibility, snapshot.Compatibility);
    }

    [Fact]
    public async Task Factory_accepts_structured_launch_without_loading_native_code()
    {
        var factory = new GhosttyTerminalSessionFactory();
        var launch = new TerminalLaunchRequest(
            "/tmp",
            "/bin/sh",
            ["-c", "printf ok"],
            new Dictionary<string, string> { ["LANG"] = "C" });

        await using var session = await factory.CreateAsync(
            SessionId.New(),
            launch,
            CancellationToken.None);

        var snapshot = await session.SnapshotAsync(CancellationToken.None);
        Assert.Equal(SessionLifecycle.Starting, snapshot.Lifecycle);
    }

    [Fact]
    public async Task Exact_grid_resize_requires_an_attached_native_renderer()
    {
        var factory = new GhosttyTerminalSessionFactory();
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest("/tmp"),
            CancellationToken.None);
        var process = Assert.IsAssignableFrom<ITerminalProcess>(session);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await process.ResizeAsync(
                new ViewportDescriptor(800, 600, 2, 80, 24),
                CancellationToken.None));
    }

    [Fact]
    public async Task Factory_honors_cancellation_before_creating_a_session()
    {
        var factory = new GhosttyTerminalSessionFactory();
        var cancellation = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await factory.CreateAsync(SessionId.New(), new TerminalLaunchRequest("/tmp"), cancellation));
    }

    private static IReadOnlyList<string?> ReadPointerArray(nint pointer, nuint count)
    {
        var values = new string?[checked((int)count)];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pointer, index * nint.Size));
        }

        return values;
    }

    private static CommandBinding Binding(CommandId commandId, string key) => new(
        commandId,
        KeySequence.Of(new KeyStroke(key, KeyModifiers.Meta)),
        CommandContext.Terminal);

    private static IReadOnlyList<KeyValuePair<string, string>> ReadEnvironment(nint pointer, nuint count)
    {
        var values = new KeyValuePair<string, string>[checked((int)count)];
        var structSize = Marshal.SizeOf<NativeEnvironmentVariableV1>();
        for (var index = 0; index < values.Length; index++)
        {
            var variable = Marshal.PtrToStructure<NativeEnvironmentVariableV1>(
                pointer + index * structSize);
            values[index] = new KeyValuePair<string, string>(
                Marshal.PtrToStringUTF8(variable.Name)!,
                Marshal.PtrToStringUTF8(variable.Value)!);
        }

        return values;
    }
}
