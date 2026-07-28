using System.Text;
using System.Runtime.InteropServices;
using GhostShell.Application;

namespace GhostShell.Terminal;

/// <summary>
/// Stable GhostSHELL-owned facade over the pinned, unversioned Ghostty embedding ABI.
/// </summary>
internal static class GhosttyNativeTerminal
{
    private const int InitialScreenBufferSize = 64 * 1024;
    private const int MaximumScreenByteCount = 8 * 1024 * 1024;

    public static GhosttyTerminalHandle Attach(nint hostView, TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The full libghostty renderer currently exposes an NSView host only on macOS.");
        }

        ConfigureResourcesDirectory();
        if (!GhosttyNativeMethods.Initialize())
        {
            throw CreateNativeException("Unable to initialize libghostty");
        }

        using var nativeOptions = GhosttyNativeLaunchOptions.Create(launch);
        var options = nativeOptions.Value;
        var nativeHandle = GhosttyNativeMethods.TerminalAttachV1(hostView, in options);
        if (nativeHandle == 0)
        {
            throw CreateNativeException("Unable to create a Ghostty terminal surface");
        }

        return new GhosttyTerminalHandle(nativeHandle);
    }

    public static bool ConfirmClose(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return GhosttyNativeMethods.TerminalConfirmClose(terminal);
    }

    public static bool NeedsCloseConfirmation(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return GhosttyNativeMethods.TerminalNeedsCloseConfirmation(terminal);
    }

    public static void Reparent(GhosttyTerminalHandle terminal, nint hostView)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (hostView == 0 || !GhosttyNativeMethods.TerminalReparent(terminal, hostView))
        {
            throw CreateNativeException("Unable to attach the terminal renderer to its native host");
        }
    }

    public static void DetachView(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        GhosttyNativeMethods.TerminalDetachView(terminal);
    }

    public static void Focus(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        GhosttyNativeMethods.TerminalFocus(terminal);
    }

    public static void Resize(
        GhosttyTerminalHandle terminal,
        double logicalWidth,
        double logicalHeight,
        double renderScale)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (logicalWidth <= 0 || logicalHeight <= 0 || renderScale <= 0)
        {
            return;
        }

        GhosttyNativeMethods.TerminalResize(
            terminal,
            logicalWidth,
            logicalHeight,
            renderScale);
    }

    public static bool ResizeGrid(
        GhosttyTerminalHandle terminal,
        int columns,
        int rows)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (columns is < 2 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        return GhosttyNativeMethods.TerminalResizeGridV1(
            terminal,
            (uint)columns,
            (uint)rows);
    }

    /// <summary>
    /// Applies typography and palette to a running terminal. Returns false when
    /// the host declines, which leaves the surface exactly as it was rather than
    /// half-reconfigured.
    /// </summary>
    public static bool UpdateRenderProfile(
        GhosttyTerminalHandle terminal,
        TerminalRenderProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(profile);
        using var marshalled = GhosttyNativeLaunchOptions.ForRenderProfile(profile);
        return GhosttyNativeMethods.TerminalUpdateRenderProfileV1(
            terminal,
            marshalled.RenderProfileBlock);
    }

    public static ulong ReadInputEpoch(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return GhosttyNativeMethods.TerminalInputEpochV1(terminal);
    }

    public static bool SendText(
        GhosttyTerminalHandle terminal,
        string text,
        ulong expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return true;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        return GhosttyNativeMethods.TerminalSendTextAtEpochV1(
            terminal,
            utf8,
            (nuint)utf8.Length,
            expectedEpoch);
    }

    public static bool PasteText(
        GhosttyTerminalHandle terminal,
        string text,
        ulong expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return true;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        return GhosttyNativeMethods.TerminalPasteTextAtEpochV1(
            terminal,
            utf8,
            (nuint)utf8.Length,
            expectedEpoch);
    }

    public static bool SendKey(
        GhosttyTerminalHandle terminal,
        TerminalKeyStroke keyStroke,
        ulong expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(keyStroke);
        return GhosttyNativeMethods.TerminalSendKeyAtEpochV1(
            terminal,
            (uint)keyStroke.Key,
            (uint)keyStroke.Modifiers,
            expectedEpoch);
    }

    public static bool SendChord(
        GhosttyTerminalHandle terminal,
        TerminalCharacterChord chord,
        ulong expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(chord);
        return GhosttyNativeMethods.TerminalSendChordAtEpochV1(
            terminal,
            chord.Character,
            (uint)chord.Modifier,
            expectedEpoch);
    }

    public static bool SendMouse(
        GhosttyTerminalHandle terminal,
        TerminalMouseInput mouseInput,
        ulong expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(mouseInput);
        return GhosttyNativeMethods.TerminalSendMouseAtEpochV1(
            terminal,
            (uint)mouseInput.Button,
            (uint)mouseInput.Kind,
            checked((uint)mouseInput.Column),
            checked((uint)mouseInput.Row),
            (uint)mouseInput.Modifiers,
            expectedEpoch);
    }

    public static string ReadScreen(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var buffer = new byte[InitialScreenBufferSize];
        while (true)
        {
            var requiredLength = GhosttyNativeMethods.TerminalReadScreen(
                terminal,
                buffer,
                (nuint)buffer.Length);

            if (requiredLength < (nuint)buffer.Length)
            {
                return Encoding.UTF8.GetString(buffer, 0, checked((int)requiredLength));
            }

            if (requiredLength > (nuint)MaximumScreenByteCount)
            {
                throw new GhosttyNativeException(
                    $"The terminal screen exceeded {MaximumScreenByteCount} UTF-8 bytes.");
            }

            buffer = new byte[checked((int)requiredLength + 1)];
        }
    }

    public static GhosttyTerminalScreenState ReadScreenState(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var state = new NativeTerminalScreenStateV1
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeTerminalScreenStateV1>()),
            Version = 1,
        };
        if (!GhosttyNativeMethods.TerminalReadScreenStateV1(terminal, ref state))
        {
            throw new GhosttyNativeException("Unable to read canonical libghostty screen state.");
        }

        var rows = checked((int)state.Rows);
        var columns = checked((int)state.Columns);
        var cursorRow = checked((int)state.CursorRow);
        var cursorColumn = checked((int)state.CursorColumn);
        if (rows <= 0 || columns <= 0 || cursorRow < 0 || cursorRow >= rows ||
            cursorColumn < 0 || cursorColumn >= columns)
        {
            throw new GhosttyNativeException("libghostty returned an invalid terminal viewport state.");
        }

        return new GhosttyTerminalScreenState(
            rows,
            columns,
            cursorRow,
            cursorColumn,
            state.AlternateScreen != 0,
            state.BracketedPaste != 0,
            state.MouseCaptured != 0);
    }

    public static string? ReadWorkingDirectory(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var value = ReadBoundedUtf8(
            terminal,
            GhosttyNativeMethods.TerminalReadWorkingDirectory,
            "terminal working directory");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool HasProcessExited(GhosttyTerminalHandle terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return GhosttyNativeMethods.TerminalProcessExited(terminal);
    }

    private static GhosttyNativeException CreateNativeException(string fallback)
    {
        var detail = GhosttyNativeMethods.GetLastError();
        return new GhosttyNativeException(string.IsNullOrWhiteSpace(detail) ? fallback : detail);
    }

    private static string ReadBoundedUtf8(
        GhosttyTerminalHandle terminal,
        Func<GhosttyTerminalHandle, byte[], nuint, nuint> read,
        string description)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var requiredLength = read(terminal, buffer, (nuint)buffer.Length);
            if (requiredLength < (nuint)buffer.Length)
            {
                return Encoding.UTF8.GetString(buffer, 0, checked((int)requiredLength));
            }

            if (requiredLength > (nuint)MaximumScreenByteCount)
            {
                throw new GhosttyNativeException(
                    $"The {description} exceeded {MaximumScreenByteCount} UTF-8 bytes.");
            }

            buffer = new byte[checked((int)requiredLength + 1)];
        }
    }

    /// <summary>
    /// Publishes the terminal engine's resources directory into the real process
    /// environment.
    ///
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> only
    /// updates the runtime's own copy on Unix; the native environment that
    /// <c>getenv</c> reads is untouched. The engine reads this variable from
    /// native code, so setting it the managed way left it invisible — the engine
    /// then found no resources directory, disabled shell integration, and without
    /// prompt markers it could never tell that a terminal was sitting idle. That
    /// is why closing an idle terminal still asked for confirmation.
    /// </summary>
    private static void ConfigureResourcesDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
        {
            return;
        }

        var resourcesDirectory = Path.Combine(AppContext.BaseDirectory, "ghostty");
        if (!Directory.Exists(resourcesDirectory))
        {
            return;
        }

        Environment.SetEnvironmentVariable("GHOSTTY_RESOURCES_DIR", resourcesDirectory);
        if (!OperatingSystem.IsWindows())
        {
            _ = SetNativeEnvironmentVariable("GHOSTTY_RESOURCES_DIR", resourcesDirectory, 1);
        }

        // Read it back the way the engine will, so the value that matters is
        // reported rather than the one we believe we set. Printed once per process:
        // whether the engine can see this directory decides whether shell
        // integration runs at all, and that is worth stating rather than assuming.
        var seenByNativeCode = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")
            : Marshal.PtrToStringAnsi(GetNativeEnvironmentVariable("GHOSTTY_RESOURCES_DIR"));
        Console.Error.WriteLine(
            $"[ghostshell:input] resources dir set to {resourcesDirectory}; "
            + $"native code reads {seenByNativeCode ?? "<null>"}");
    }

    [DllImport("libc", EntryPoint = "getenv", CharSet = CharSet.Ansi)]
    private static extern nint GetNativeEnvironmentVariable(string name);

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetNativeEnvironmentVariable(
        string name,
        string value,
        int overwrite);
}

internal readonly record struct GhosttyTerminalScreenState(
    int Rows,
    int Columns,
    int CursorRow,
    int CursorColumn,
    bool IsAlternateScreen,
    bool IsBracketedPasteEnabled,
    bool IsMouseTrackingEnabled);
