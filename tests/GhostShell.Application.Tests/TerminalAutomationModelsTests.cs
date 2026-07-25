namespace GhostShell.Application.Tests;

public sealed class TerminalAutomationModelsTests
{
    [Fact]
    public void Agent_input_barrier_has_a_stable_explicit_capability_name()
    {
        Assert.Equal(
            "terminal.agent_input_barrier",
            SessionCapabilities.TerminalAgentInputBarrier);
    }

    [Fact]
    public void Character_chord_has_a_stable_distinct_capability_name()
    {
        Assert.Equal(
            "terminal.send_chord",
            SessionCapabilities.TerminalSendChord);
        Assert.NotEqual(
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalSendChord);
    }

    [Fact]
    public void Enter_interrupt_and_wait_have_distinct_stable_operation_and_capability_names()
    {
        Assert.Equal("terminal.enter", ApplicationOperations.TerminalEnter);
        Assert.Equal("terminal.interrupt", ApplicationOperations.TerminalInterrupt);
        Assert.Equal("terminal.wait", ApplicationOperations.TerminalWait);
        Assert.Equal("terminal.enter", SessionCapabilities.TerminalEnter);
        Assert.Equal("terminal.interrupt", SessionCapabilities.TerminalInterrupt);
        Assert.Equal("terminal.wait", SessionCapabilities.TerminalWait);
        Assert.Equal(
            3,
            new[]
            {
                ApplicationOperations.TerminalEnter,
                ApplicationOperations.TerminalInterrupt,
                ApplicationOperations.TerminalWait,
            }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Wait_inputs_require_finite_positive_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForTextInput("ready", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForChangeInput(0, TimeSpan.FromMinutes(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForStableInput(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalWaitForChangeInput(-1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Changed_outcome_requires_a_newer_content_revision()
    {
        var snapshot = Screen(contentRevision: 3);

        Assert.Throws<ArgumentException>(() =>
            TerminalWaitOutcome.Changed(snapshot, initialContentRevision: 3));

        var changed = TerminalWaitOutcome.Changed(snapshot, initialContentRevision: 2);
        Assert.Equal(TerminalWaitOutcomeKind.Changed, changed.Kind);
        Assert.Equal(2, changed.InitialContentRevision);
        Assert.Equal(3, changed.ObservedContentRevision);
    }

    [Theory]
    [InlineData(TerminalWaitOutcomeKind.Matched)]
    [InlineData(TerminalWaitOutcomeKind.Changed)]
    [InlineData(TerminalWaitOutcomeKind.Stable)]
    public void Successful_wait_outcomes_require_the_observed_screen(
        TerminalWaitOutcomeKind kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new TerminalWaitOutcome(kind, null, 0));
        Assert.Throws<ArgumentException>(() =>
            new TerminalWaitOutcome(kind, Screen(contentRevision: 0), null));
    }

    [Theory]
    [InlineData(TerminalWaitOutcomeKind.Timeout)]
    [InlineData(TerminalWaitOutcomeKind.Cancelled)]
    [InlineData(TerminalWaitOutcomeKind.SessionEnded)]
    public void Terminal_wait_end_states_can_precede_the_first_screen_read(
        TerminalWaitOutcomeKind kind)
    {
        var outcome = new TerminalWaitOutcome(kind, null, null);

        Assert.Null(outcome.Snapshot);
        Assert.Null(outcome.ObservedContentRevision);
    }

    private static TerminalScreenSnapshot Screen(long contentRevision) => new(
        string.Empty,
        0,
        0,
        24,
        80,
        false,
        null,
        DateTimeOffset.UnixEpoch,
        ContentRevision: contentRevision);
}
