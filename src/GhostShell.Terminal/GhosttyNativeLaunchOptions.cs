using System.Runtime.InteropServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

/// <summary>
/// Owns all unmanaged memory referenced by one versioned native launch-options call.
/// </summary>
internal sealed class GhosttyNativeLaunchOptions : IDisposable
{
    internal const uint OptionsVersion = 1;
    internal const ulong EstimatedBytesPerScrollbackLine = 256;

    private readonly List<nint> _allocations = [];
    private bool _disposed;

    private GhosttyNativeLaunchOptions()
    {
    }

    internal NativeTerminalOptionsV1 Value { get; private set; }

    internal static GhosttyNativeLaunchOptions Create(TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var owner = new GhosttyNativeLaunchOptions();
        try
        {
            var keybindings = launch.Keymap is null
                ? []
                : GhosttyTerminalKeymap.CreateBindings(launch.Keymap);
            owner.Value = new NativeTerminalOptionsV1
            {
                StructSize = checked((uint)Marshal.SizeOf<NativeTerminalOptionsV1>()),
                Version = OptionsVersion,
                WorkingDirectory = owner.AllocateString(launch.WorkingDirectory),
                Executable = owner.AllocateString(launch.Executable),
                Arguments = owner.AllocateArguments(launch.Arguments),
                ArgumentCount = checked((nuint)launch.Arguments.Count),
                Environment = owner.AllocateEnvironment(launch.Environment),
                EnvironmentCount = checked((nuint)launch.Environment.Count),
                RenderProfile = owner.AllocateRenderProfile(launch.RenderProfile),
                TerminalKeybindings = owner.AllocateStrings(keybindings),
                TerminalKeybindingCount = checked((nuint)keybindings.Count),
                TerminalKeymapPresent = launch.Keymap is null ? 0U : 1U,
            };
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (var index = _allocations.Count - 1; index >= 0; index--)
        {
            Marshal.FreeCoTaskMem(_allocations[index]);
        }

        _allocations.Clear();
        _disposed = true;
    }

    private nint AllocateArguments(IReadOnlyList<string> arguments)
    {
        return AllocateStrings(arguments);
    }

    private nint AllocateStrings(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var block = AllocateBlock(checked(nint.Size * values.Count));
        for (var index = 0; index < values.Count; index++)
        {
            Marshal.WriteIntPtr(block, checked(index * nint.Size), AllocateString(values[index]));
        }

        return block;
    }

    private nint AllocateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count == 0)
        {
            return 0;
        }

        var structSize = Marshal.SizeOf<NativeEnvironmentVariableV1>();
        var block = AllocateBlock(checked(structSize * environment.Count));
        var index = 0;
        foreach (var variable in environment.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var nativeVariable = new NativeEnvironmentVariableV1
            {
                Name = AllocateString(variable.Key),
                Value = AllocateString(variable.Value),
            };
            Marshal.StructureToPtr(nativeVariable, block + checked(index * structSize), false);
            index++;
        }

        return block;
    }

    /// <summary>
    /// Marshals a render profile on its own, for reconfiguring a surface that is
    /// already running. The caller owns the returned object and must dispose it
    /// once the native call has returned.
    /// </summary>
    public static GhosttyNativeLaunchOptions ForRenderProfile(
        TerminalRenderProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var owner = new GhosttyNativeLaunchOptions();
        try
        {
            owner.RenderProfileBlock = owner.AllocateRenderProfile(profile);
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>The marshalled profile, when this owner carries one alone.</summary>
    public nint RenderProfileBlock { get; private set; }

    private nint AllocateRenderProfile(TerminalRenderProfileSnapshot? profile)
    {
        if (profile is null)
        {
            return 0;
        }

        var palette = AllocateBlock(checked(sizeof(uint) * profile.Palette.AnsiColors.Count));
        for (var index = 0; index < profile.Palette.AnsiColors.Count; index++)
        {
            Marshal.WriteInt32(
                palette,
                checked(index * sizeof(uint)),
                unchecked((int)EncodeColor(profile.Palette.AnsiColors[index])));
        }

        var nativeProfile = new NativeTerminalRenderProfileV1
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeTerminalRenderProfileV1>()),
            FontSize = checked((float)profile.FontSize),
            CursorStyle = profile.CursorStyle switch
            {
                TerminalCursorStyle.Block => 0,
                TerminalCursorStyle.Bar => 1,
                TerminalCursorStyle.Underline => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown terminal cursor style."),
            },
            CursorBlink = profile.CursorBlink ? 1U : 0U,
            ScrollbackLimitBytes = checked((ulong)profile.ScrollbackLines * EstimatedBytesPerScrollbackLine),
            ForegroundRgb = EncodeColor(profile.Palette.Foreground),
            BackgroundRgb = EncodeColor(profile.Palette.Background),
            CursorRgb = EncodeColor(profile.Palette.Cursor),
            SelectionBackgroundRgb = EncodeColor(profile.Palette.SelectionBackground),
            AnsiPaletteRgb = palette,
            AnsiPaletteCount = checked((nuint)profile.Palette.AnsiColors.Count),
            FontFamily = AllocateString(profile.FontFamily),
            LineHeight = profile.LineHeight,
            ClipboardRead = profile.ClipboardPolicy.ReadAccess switch
            {
                TerminalClipboardAccess.Ask => 0,
                TerminalClipboardAccess.Allow => 1,
                TerminalClipboardAccess.Deny => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown clipboard-read policy."),
            },
            ClipboardWrite = profile.ClipboardPolicy.WriteAccess switch
            {
                TerminalClipboardAccess.Ask => 0,
                TerminalClipboardAccess.Allow => 1,
                TerminalClipboardAccess.Deny => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown clipboard-write policy."),
            },
            PasteSafety = profile.ClipboardPolicy.PasteSafety switch
            {
                TerminalPasteSafetyPolicy.ProtectUnsafe => 0,
                TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed => 1,
                TerminalPasteSafetyPolicy.AllowUnsafe => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown paste-safety policy."),
            },
            LinkPolicy = profile.LinkPolicy switch
            {
                TerminalLinkPolicy.ConfirmBeforeOpen => 0,
                TerminalLinkPolicy.Open => 1,
                TerminalLinkPolicy.Disabled => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown terminal link policy."),
            },
            ImeEnabled = profile.ImeEnabled ? 1U : 0U,
            ShellIntegration = profile.ShellIntegration switch
            {
                TerminalShellIntegrationMode.Detect => 0,
                TerminalShellIntegrationMode.Disabled => 1,
                TerminalShellIntegrationMode.Bash => 2,
                TerminalShellIntegrationMode.Elvish => 3,
                TerminalShellIntegrationMode.Fish => 4,
                TerminalShellIntegrationMode.Nushell => 5,
                TerminalShellIntegrationMode.Zsh => 6,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown shell-integration mode."),
            },
            BellMode = profile.BellMode switch
            {
                TerminalBellMode.Visual => 0,
                TerminalBellMode.System => 1,
                TerminalBellMode.SystemAndVisual => 2,
                TerminalBellMode.Disabled => 3,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown terminal bell mode."),
            },
            Compatibility = profile.Compatibility switch
            {
                TerminalCompatibilityProfile.Ghostty => 0,
                TerminalCompatibilityProfile.Xterm256Color => 1,
                TerminalCompatibilityProfile.Legacy => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown compatibility profile."),
            },
        };

        var block = AllocateBlock(Marshal.SizeOf<NativeTerminalRenderProfileV1>());
        Marshal.StructureToPtr(nativeProfile, block, false);
        return block;
    }

    private nint AllocateString(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        _allocations.Add(pointer);
        return pointer;
    }

    private nint AllocateBlock(int byteCount)
    {
        var pointer = Marshal.AllocCoTaskMem(byteCount);
        _allocations.Add(pointer);
        return pointer;
    }

    private static uint EncodeColor(RgbColor color) =>
        ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTerminalOptionsV1
{
    public uint StructSize;
    public uint Version;
    public nint WorkingDirectory;
    public nint Executable;
    public nint Arguments;
    public nuint ArgumentCount;
    public nint Environment;
    public nuint EnvironmentCount;
    public nint RenderProfile;
    public nint TerminalKeybindings;
    public nuint TerminalKeybindingCount;
    public uint TerminalKeymapPresent;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEnvironmentVariableV1
{
    public nint Name;
    public nint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTerminalRenderProfileV1
{
    public uint StructSize;
    public float FontSize;
    public uint CursorStyle;
    public uint CursorBlink;
    public ulong ScrollbackLimitBytes;
    public uint ForegroundRgb;
    public uint BackgroundRgb;
    public uint CursorRgb;
    public uint SelectionBackgroundRgb;
    public nint AnsiPaletteRgb;
    public nuint AnsiPaletteCount;
    public nint FontFamily;
    public double LineHeight;
    public uint ClipboardRead;
    public uint ClipboardWrite;
    public uint PasteSafety;
    public uint LinkPolicy;
    public uint ImeEnabled;
    public uint ShellIntegration;
    public uint BellMode;
    public uint Compatibility;
}
