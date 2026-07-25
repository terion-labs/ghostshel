using System.Runtime.InteropServices;

namespace GhostShell.Terminal;

/// <summary>
/// Keeps native discovery at the GhostSHELL shim boundary. The shim pins and hides
/// libghostty's unversioned embedding ABI.
/// </summary>
public static class GhosttyLibraryProbe
{
    private static readonly string[] RequiredExports =
    [
        "ghostshell_ghostty_initialize",
        "ghostshell_ghostty_last_error",
        "ghostshell_terminal_attach",
        "ghostshell_terminal_attach_v1",
        "ghostshell_terminal_confirm_close",
        "ghostshell_terminal_needs_close_confirmation",
        "ghostshell_terminal_reparent",
        "ghostshell_terminal_detach_view",
        "ghostshell_terminal_detach",
        "ghostshell_terminal_set_host_key_interceptor_v1",
        "ghostshell_terminal_set_physical_input_gate_v1",
        "ghostshell_terminal_input_epoch_v1",
        "ghostshell_terminal_focus",
        "ghostshell_terminal_resize",
        "ghostshell_terminal_resize_grid_v1",
        "ghostshell_terminal_send_text",
        "ghostshell_terminal_send_text_at_epoch_v1",
        "ghostshell_terminal_paste_text",
        "ghostshell_terminal_paste_text_at_epoch_v1",
        "ghostshell_terminal_send_key",
        "ghostshell_terminal_send_key_at_epoch_v1",
        "ghostshell_terminal_send_chord_at_epoch_v1",
        "ghostshell_terminal_send_mouse",
        "ghostshell_terminal_send_mouse_at_epoch_v1",
        "ghostshell_terminal_read_screen_state_v1",
        "ghostshell_terminal_read_working_directory",
        "ghostshell_terminal_read_screen",
        "ghostshell_terminal_process_exited",
    ];

    public static GhosttyAvailability Detect()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new GhosttyAvailability(
                false,
                null,
                "The native libghostty renderer is currently available on macOS arm64 only.");
        }

        if (TryLoadCompatible(out var handle, out var candidate))
        {
            NativeLibrary.Free(handle);
            return new GhosttyAvailability(true, candidate, "libghostty 1.3.1 · Metal · available");
        }

        return new GhosttyAvailability(
            false,
            null,
            "A compatible native libghostty runtime is missing. Run ./scripts/bootstrap.sh.");
    }

    internal static bool TryLoadCompatible(out nint handle, out string? loadedPath)
    {
        foreach (var candidate in GetCandidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out handle))
            {
                continue;
            }

            if (HasRequiredExports(handle))
            {
                loadedPath = candidate;
                return true;
            }

            NativeLibrary.Free(handle);
        }

        handle = 0;
        loadedPath = null;
        return false;
    }

    private static bool HasRequiredExports(nint handle) =>
        RequiredExports.All(export => NativeLibrary.TryGetExport(handle, export, out _));

    private static IReadOnlyList<string> GetCandidates()
    {
        var configuredPath = Environment.GetEnvironmentVariable("GHOSTSHELL_GHOSTTY_SHIM_PATH");
        string[] platformCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, GhosttyNativeMethods.LibraryName),
            GhosttyNativeMethods.LibraryName,
        ];

        return string.IsNullOrWhiteSpace(configuredPath)
            ? platformCandidates
            : [configuredPath, .. platformCandidates];
    }
}
