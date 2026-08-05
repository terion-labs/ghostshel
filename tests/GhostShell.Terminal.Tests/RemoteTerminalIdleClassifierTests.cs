namespace GhostShell.Terminal.Tests;

public sealed class RemoteTerminalIdleClassifierTests
{
    [Theory]
    [InlineData("root@ubuntu:~# ", 15)]
    [InlineData("deploy@host:/srv$ ", 18)]
    [InlineData("[user@host project]$ ", 21)]
    [InlineData("user@host% ", 11)]
    [InlineData("PS C:\\Users\\deploy> ", 19)]
    public void Common_remote_shell_prompts_are_idle(string line, int cursorColumn)
    {
        var state = State(cursorColumn);

        Assert.True(IsAtShellPrompt(line, state));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("root@ubuntu:~# sleep 100\n", 0)]
    [InlineData("mysql> ", 7)]
    [InlineData("building package 42%", 20)]
    public void Running_or_non_shell_interactions_remain_confirmation_worthy(
        string screen,
        int cursorColumn)
    {
        var state = State(cursorColumn);

        Assert.False(IsAtShellPrompt(screen, state));
    }

    /// <summary>
    /// Bracketed paste is what a shell turns on at its prompt so that a paste
    /// arrives as text rather than as commands. Every modern bash and zsh does
    /// it, so treating it as a sign of activity meant every remote shell looked
    /// busy and every close asked to confirm.
    /// </summary>
    [Fact]
    public void A_prompt_that_protects_pastes_is_still_a_prompt()
    {
        var state = State(cursorColumn: 15) with
        {
            IsBracketedPasteEnabled = true,
        };

        Assert.True(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    /// <summary>
    /// What actually means something else has the terminal: a program drawing
    /// on the alternate screen, or one reading the mouse. Both still say so
    /// even where a prompt would otherwise be recognised.
    /// </summary>
    [Fact]
    public void Mouse_tracking_is_never_treated_as_an_idle_shell()
    {
        var state = State(cursorColumn: 15) with
        {
            IsMouseTrackingEnabled = true,
        };

        Assert.False(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    [Fact]
    public void Alternate_screen_is_never_treated_as_an_idle_shell()
    {
        var state = State(cursorColumn: 15) with
        {
            IsAlternateScreen = true,
        };

        Assert.False(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    private static bool IsAtShellPrompt(string screen, ScreenState state) =>
        RemoteTerminalIdleClassifier.IsAtShellPrompt(
            screen,
            state.CursorRow,
            state.CursorColumn,
            state.IsAlternateScreen,
            state.IsBracketedPasteEnabled,
            state.IsMouseTrackingEnabled);

    private static ScreenState State(int cursorColumn) =>
        new(
            CursorRow: 0,
            CursorColumn: cursorColumn,
            IsAlternateScreen: false,
            IsBracketedPasteEnabled: false,
            IsMouseTrackingEnabled: false);

    private readonly record struct ScreenState(
        int CursorRow,
        int CursorColumn,
        bool IsAlternateScreen,
        bool IsBracketedPasteEnabled,
        bool IsMouseTrackingEnabled);
}
