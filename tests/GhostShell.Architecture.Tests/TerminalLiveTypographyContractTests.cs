namespace GhostShell.Architecture.Tests;

/// <summary>
/// Saving a new terminal font used to change the stored definition and nothing on
/// screen. The first fix wired only the managed surface — but macOS renders
/// through the native Ghostty host, so on the platform the bug was reported from
/// it still did nothing.
///
/// The chain has five links in four projects and no single test exercises it end
/// to end, so each link is pinned here. A break in any one of them puts the
/// setting back to doing nothing.
/// </summary>
public sealed class TerminalLiveTypographyContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));

    [Fact]
    public void The_native_shim_reconfigures_a_live_surface()
    {
        var shim = Read("native", "macos", "GhostShellGhostty.m");

        Assert.Contains(
            "bool ghostshell_terminal_update_render_profile_v1(",
            shim,
            StringComparison.Ordinal);

        // Ghostty applies a configuration to an existing surface. Recreating the
        // surface would apply the same values and lose the scrollback with them.
        Assert.Contains("ghostty_surface_update_config(surface, config)", shim, StringComparison.Ordinal);

        // The whole configuration is rebuilt, so the launch keymap has to be
        // reapplied — otherwise changing the font clears the terminal's keybinds.
        Assert.Contains("gs_apply_retained_keymap(config, view.launchKeybindings)", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void The_entry_point_is_declared_and_probed()
    {
        Assert.Contains(
            "ghostshell_terminal_update_render_profile_v1",
            Read("native", "macos", "GhostShellGhostty.h"),
            StringComparison.Ordinal);

        // The probe is what turns a missing symbol into a clear startup failure
        // rather than a crash at first use.
        Assert.Contains(
            "\"ghostshell_terminal_update_render_profile_v1\"",
            Read("src", "GhostShell.Terminal", "GhosttyLibraryProbe.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_applies_a_profile_without_restarting()
    {
        Assert.Contains(
            "public ValueTask<bool> UpdateRenderProfileAsync(",
            Read("src", "GhostShell.Terminal", "GhosttyTerminalSession.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "GhosttyNativeTerminal.UpdateRenderProfile(terminal, renderProfile)",
            Read("src", "GhostShell.Terminal", "GhosttyTerminalSession.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_host_exposes_the_operation()
    {
        Assert.Contains(
            "UpdateTerminalRenderProfileAsync",
            Read("src", "GhostShell.Application", "ISessionHostClient.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "public async ValueTask<HostResult<bool>> UpdateTerminalRenderProfileAsync(",
            Read("src", "GhostShell.SessionHost", "InMemorySessionHostClient.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Both presentation paths have to be fed. Wiring only one is exactly the
    /// mistake that made the first fix a no-op on macOS.
    /// </summary>
    [Fact]
    public void Both_presentation_hosts_receive_the_live_profile()
    {
        var host = Read("src", "GhostShell.App", "Controls", "TerminalPresentationHost.cs");

        Assert.Contains("_nativeHost.RenderProfile = RenderProfile;", host, StringComparison.Ordinal);
        Assert.Contains("_managedHost.RenderProfile = RenderProfile;", host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_native_host_pushes_the_profile_without_restarting_the_session()
    {
        var native = Read("src", "GhostShell.App", "Controls", "TerminalSessionHost.cs");

        Assert.Contains("change.Property == RenderProfileProperty", native, StringComparison.Ordinal);
        Assert.Contains("UpdateTerminalRenderProfileAsync(", native, StringComparison.Ordinal);

        // A restart here would defeat the point of the whole chain.
        var branch = native[native.IndexOf(
            "change.Property == RenderProfileProperty",
            StringComparison.Ordinal)..];
        var nextBranch = branch.IndexOf("else if", StringComparison.Ordinal);
        Assert.DoesNotContain("RestartSession", branch[..nextBranch], StringComparison.Ordinal);
    }

    /// <summary>
    /// Avalonia does not clip a native child view to its parent, and does not see
    /// focus move into one. Both are reported across the boundary explicitly.
    /// </summary>
    [Fact]
    public void The_native_view_is_told_its_corner_radius_and_reports_its_focus()
    {
        var shim = Read("native", "macos", "GhostShellGhostty.m");

        Assert.Contains("applyHostCornerTopLeft:", shim, StringComparison.Ordinal);

        // A mask, not a corner radius: the surface draws through a Metal layer,
        // whose content a layer's cornerRadius does not clip. The mask is also
        // rebuilt on resize, or it would crop the terminal instead of rounding it.
        //
        // It is built corner by corner rather than from a rounded rect, because a
        // terminal usually sits below a panel header — only its bottom corners are
        // at the panel's edge, and rounding all four carved notches into the middle
        // of the panel.
        Assert.Contains("CGPathAddArcToPoint", shim, StringComparison.Ordinal);
        Assert.DoesNotContain("CGPathCreateWithRoundedRect", shim, StringComparison.Ordinal);
        Assert.Contains("self.layer.mask = mask;", shim, StringComparison.Ordinal);
        var resize = shim[shim.IndexOf(
            "- (void)setFrameSize:(NSSize)newSize",
            StringComparison.Ordinal)..];
        Assert.Contains("updateHostCornerMask", resize[..300], StringComparison.Ordinal);
        Assert.Contains("self.focusObserver(self.focusObserverUserdata)", shim, StringComparison.Ordinal);

        // Focus is reported from becomeFirstResponder, which is the only place
        // that sees a click land on the native view.
        var responder = shim[shim.IndexOf(
            "- (BOOL)becomeFirstResponder",
            StringComparison.Ordinal)..];
        Assert.Contains("focusObserver", responder[..400], StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_activates_the_panel_from_native_focus()
    {
        Assert.Contains(
            "TerminalFocusGained=\"OnRuntimePanelTerminalFocused\"",
            Read("src", "GhostShell.App", "Views", "MainWindow.axaml"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ActivateRuntimePanelAsync(sender)",
            Read("src", "GhostShell.App", "Views", "MainWindow.RuntimeWorkspace.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Taking focus is the host deciding which panel owns the keyboard; the gate
    /// decides whether input reaches the shell. Gating focus too made a click on
    /// the terminal body do nothing at all.
    /// </summary>
    [Fact]
    public void A_click_takes_focus_before_the_input_gate_is_consulted()
    {
        var shim = Read("native", "macos", "GhostShellGhostty.m");
        var mouseDown = shim[shim.IndexOf(
            "- (void)mouseDown:(NSEvent *)event {",
            StringComparison.Ordinal)..];
        var body = mouseDown[..mouseDown.IndexOf('}')];

        var focus = body.IndexOf("makeFirstResponder", StringComparison.Ordinal);
        var gate = body.IndexOf("acceptPhysicalInput", StringComparison.Ordinal);

        Assert.True(focus >= 0 && gate >= 0);
        Assert.True(focus < gate, "The gate is consulted before focus is taken.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the GhostSHELL repository root.");
    }
}
