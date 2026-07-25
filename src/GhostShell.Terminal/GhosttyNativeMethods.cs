using System.Runtime.InteropServices;
using System.Reflection;

namespace GhostShell.Terminal;

internal static class GhosttyNativeMethods
{
    internal const string LibraryName = "libghostshell-ghostty.dylib";

    static GhosttyNativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(GhosttyNativeMethods).Assembly, ResolveLibrary);
    }

    [DllImport(LibraryName, EntryPoint = "ghostshell_ghostty_initialize")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Initialize();

    [DllImport(LibraryName, EntryPoint = "ghostshell_ghostty_last_error")]
    private static extern nint LastError();

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_attach")]
    internal static extern nint TerminalAttach(
        nint hostView,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? workingDirectory);

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_attach_v1")]
    internal static extern nint TerminalAttachV1(
        nint hostView,
        in NativeTerminalOptionsV1 options);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_confirm_close")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalConfirmClose(GhosttyTerminalHandle terminal);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_needs_close_confirmation")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalNeedsCloseConfirmation(GhosttyTerminalHandle terminal);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_reparent")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalReparent(GhosttyTerminalHandle terminal, nint hostView);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_detach_view")]
    internal static extern void TerminalDetachView(GhosttyTerminalHandle terminal);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_detach")]
    internal static extern void TerminalDetach(nint terminal);

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_set_host_key_interceptor_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSetHostKeyInterceptorV1(
        GhosttyTerminalHandle terminal,
        NativeTerminalHostKeyInterceptorV1? interceptor,
        nint userdata);

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_set_physical_input_gate_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSetPhysicalInputGateV1(
        GhosttyTerminalHandle terminal,
        NativeTerminalPhysicalInputGateV1? gate,
        nint userdata);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_input_epoch_v1")]
    internal static extern ulong TerminalInputEpochV1(GhosttyTerminalHandle terminal);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_focus")]
    internal static extern void TerminalFocus(GhosttyTerminalHandle terminal);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_resize")]
    internal static extern void TerminalResize(
        GhosttyTerminalHandle terminal,
        double width,
        double height,
        double scale);

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_resize_grid_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalResizeGridV1(
        GhosttyTerminalHandle terminal,
        uint columns,
        uint rows);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_text")]
    internal static extern void TerminalSendText(
        GhosttyTerminalHandle terminal,
        byte[] utf8,
        nuint length);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_text_at_epoch_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendTextAtEpochV1(
        GhosttyTerminalHandle terminal,
        byte[] utf8,
        nuint length,
        ulong expectedEpoch);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_paste_text")]
    internal static extern void TerminalPasteText(
        GhosttyTerminalHandle terminal,
        byte[] utf8,
        nuint length);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_paste_text_at_epoch_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalPasteTextAtEpochV1(
        GhosttyTerminalHandle terminal,
        byte[] utf8,
        nuint length,
        ulong expectedEpoch);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_key")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendKey(
        GhosttyTerminalHandle terminal,
        uint key,
        uint modifiers);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_key_at_epoch_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendKeyAtEpochV1(
        GhosttyTerminalHandle terminal,
        uint key,
        uint modifiers,
        ulong expectedEpoch);

    [DllImport(
        LibraryName,
        EntryPoint = "ghostshell_terminal_send_chord_at_epoch_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendChordAtEpochV1(
        GhosttyTerminalHandle terminal,
        uint character,
        uint modifier,
        ulong expectedEpoch);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_mouse")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendMouse(
        GhosttyTerminalHandle terminal,
        uint button,
        uint eventKind,
        uint column,
        uint row,
        uint modifiers);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_send_mouse_at_epoch_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalSendMouseAtEpochV1(
        GhosttyTerminalHandle terminal,
        uint button,
        uint eventKind,
        uint column,
        uint row,
        uint modifiers,
        ulong expectedEpoch);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_read_screen_state_v1")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalReadScreenStateV1(
        GhosttyTerminalHandle terminal,
        ref NativeTerminalScreenStateV1 state);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_read_working_directory")]
    internal static extern nuint TerminalReadWorkingDirectory(
        GhosttyTerminalHandle terminal,
        byte[] buffer,
        nuint capacity);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_read_screen")]
    internal static extern nuint TerminalReadScreen(
        GhosttyTerminalHandle terminal,
        byte[] buffer,
        nuint capacity);

    [DllImport(LibraryName, EntryPoint = "ghostshell_terminal_process_exited")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TerminalProcessExited(GhosttyTerminalHandle terminal);

    internal static string? GetLastError() => Marshal.PtrToStringUTF8(LastError());

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (libraryName != LibraryName)
        {
            return 0;
        }

        return GhosttyLibraryProbe.TryLoadCompatible(out var handle, out _) ? handle : 0;
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
internal delegate bool NativeTerminalHostKeyInterceptorV1(
    nint userdata,
    in NativeTerminalHostKeyEventV1 keyEvent);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
internal delegate bool NativeTerminalPhysicalInputGateV1(
    nint userdata,
    in NativeTerminalPhysicalInputEventV1 inputEvent);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeTerminalHostKeyEventV1
{
    internal NativeTerminalHostKeyEventV1(
        uint physicalKey,
        uint codepoint,
        uint modifiers,
        bool isRepeat,
        uint version = 1,
        uint? structSize = null)
    {
        StructSize = structSize ?? checked((uint)Marshal.SizeOf<NativeTerminalHostKeyEventV1>());
        Version = version;
        PhysicalKey = physicalKey;
        Codepoint = codepoint;
        Modifiers = modifiers;
        IsRepeat = isRepeat ? 1U : 0U;
    }

    public readonly uint StructSize;
    public readonly uint Version;
    public readonly uint PhysicalKey;
    public readonly uint Codepoint;
    public readonly uint Modifiers;
    public readonly uint IsRepeat;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeTerminalPhysicalInputEventV1
{
    internal NativeTerminalPhysicalInputEventV1(
        uint kind,
        ulong authorityEpoch,
        uint version = 1,
        uint? structSize = null)
    {
        StructSize = structSize
            ?? checked((uint)Marshal.SizeOf<NativeTerminalPhysicalInputEventV1>());
        Version = version;
        Kind = kind;
        Reserved = 0;
        AuthorityEpoch = authorityEpoch;
    }

    public readonly uint StructSize;
    public readonly uint Version;
    public readonly uint Kind;
    public readonly uint Reserved;
    public readonly ulong AuthorityEpoch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTerminalScreenStateV1
{
    internal uint StructSize;
    internal uint Version;
    internal uint Rows;
    internal uint Columns;
    internal uint CursorRow;
    internal uint CursorColumn;
    internal uint AlternateScreen;
    internal uint BracketedPaste;
    internal uint MouseCaptured;
}
