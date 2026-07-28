namespace GhostShell.Architecture.Tests;

/// <summary>
/// Native terminal input must remain a platform subsystem. Keeping AppKit
/// composition inside the renderer view previously made focus, authority, and
/// committed-text bugs difficult to isolate or test.
/// </summary>
public sealed class TerminalPlatformInputAdapterContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void The_macos_terminal_view_forwards_input_to_a_concrete_adapter()
    {
        var view = Read("native", "macos", "GhostShellGhostty.m");

        Assert.Contains(
            "GhostShellMacInputAdapter *inputAdapter",
            view,
            StringComparison.Ordinal);
        Assert.Contains("[self.inputAdapter keyDown:event]", view, StringComparison.Ordinal);
        Assert.Contains(
            "[self.inputAdapter insertText:string replacementRange:replacementRange]",
            view,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[self interpretKeyEvents:", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_keeps_physical_preedit_and_commit_events_distinct()
    {
        var adapter = Read(
            "native",
            "macos",
            "GhostShellMacInputAdapter.m");

        Assert.Contains(
            "GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN",
            adapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_PHYSICAL_INPUT_IME_PREEDIT",
            adapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_PHYSICAL_INPUT_IME_COMMIT",
            adapter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_text_is_owned_before_preedit_is_cleared()
    {
        var adapter = Read(
            "native",
            "macos",
            "GhostShellMacInputAdapter.m");
        var insertText = adapter[
            adapter.IndexOf("- (void)insertText:", StringComparison.Ordinal)..];
        insertText = insertText[..insertText.IndexOf(
            "- (void)doCommandBySelector:",
            StringComparison.Ordinal)];

        var copy = insertText.IndexOf("string copy]", StringComparison.Ordinal);
        var clear = insertText.IndexOf("[self clearMarkedText]", StringComparison.Ordinal);

        Assert.True(copy >= 0, "The IME commit is not copied into owned storage.");
        Assert.True(clear > copy, "Preedit is cleared before the commit is owned.");
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));

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

        throw new DirectoryNotFoundException(
            "Unable to locate the GhostSHELL repository root.");
    }
}
