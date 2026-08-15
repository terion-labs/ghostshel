using Avalonia.Input;
using GhostShell.App.Views;

namespace GhostShell.App.Tests;

public sealed class AgentWorkspaceViewInputTests
{
    [Fact]
    public void Enter_submits_while_shift_enter_remains_a_newline()
    {
        Assert.True(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.None));
        Assert.False(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.Shift));
        Assert.False(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.Control));
    }
}
