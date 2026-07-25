namespace GhostShell.Application;

public enum TerminalWaitOutcomeKind
{
    Matched,
    Changed,
    Stable,
    Timeout,
    Cancelled,
    SessionEnded,
}

public sealed record TerminalWaitOutcome
{
    public TerminalWaitOutcome(
        TerminalWaitOutcomeKind Kind,
        TerminalScreenSnapshot? Snapshot,
        long? InitialContentRevision)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (InitialContentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialContentRevision));
        }

        if ((Kind is TerminalWaitOutcomeKind.Matched
                or TerminalWaitOutcomeKind.Changed
                or TerminalWaitOutcomeKind.Stable)
            && Snapshot is null)
        {
            throw new ArgumentException(
                $"A {Kind} terminal wait outcome requires a screen snapshot.",
                nameof(Snapshot));
        }

        if ((Kind is TerminalWaitOutcomeKind.Matched
                or TerminalWaitOutcomeKind.Changed
                or TerminalWaitOutcomeKind.Stable)
            && InitialContentRevision is null)
        {
            throw new ArgumentException(
                $"A {Kind} terminal wait outcome requires an initial content revision.",
                nameof(InitialContentRevision));
        }

        if (Kind == TerminalWaitOutcomeKind.Changed
            && (InitialContentRevision is null
                || Snapshot!.ContentRevision <= InitialContentRevision))
        {
            throw new ArgumentException(
                "A changed terminal wait outcome requires a newer content revision.",
                nameof(Snapshot));
        }

        this.Kind = Kind;
        this.Snapshot = Snapshot;
        this.InitialContentRevision = InitialContentRevision;
    }

    public TerminalWaitOutcomeKind Kind { get; }

    public TerminalScreenSnapshot? Snapshot { get; }

    public long? InitialContentRevision { get; }

    public long? ObservedContentRevision => Snapshot?.ContentRevision;

    public static TerminalWaitOutcome Matched(
        TerminalScreenSnapshot snapshot,
        long initialContentRevision) =>
        new(TerminalWaitOutcomeKind.Matched, snapshot, initialContentRevision);

    public static TerminalWaitOutcome Changed(
        TerminalScreenSnapshot snapshot,
        long initialContentRevision) =>
        new(TerminalWaitOutcomeKind.Changed, snapshot, initialContentRevision);

    public static TerminalWaitOutcome Stable(
        TerminalScreenSnapshot snapshot,
        long initialContentRevision) =>
        new(TerminalWaitOutcomeKind.Stable, snapshot, initialContentRevision);

    public static TerminalWaitOutcome Timeout(
        TerminalScreenSnapshot? snapshot,
        long? initialContentRevision) =>
        new(TerminalWaitOutcomeKind.Timeout, snapshot, initialContentRevision);

    public static TerminalWaitOutcome Cancelled(
        TerminalScreenSnapshot? snapshot,
        long? initialContentRevision) =>
        new(TerminalWaitOutcomeKind.Cancelled, snapshot, initialContentRevision);

    public static TerminalWaitOutcome SessionEnded(
        TerminalScreenSnapshot? snapshot,
        long? initialContentRevision) =>
        new(TerminalWaitOutcomeKind.SessionEnded, snapshot, initialContentRevision);
}

public sealed record TerminalWaitForTextInput
{
    public const int MaximumTextCharacters = 64 * 1024;

    public TerminalWaitForTextInput(string Text, TimeSpan Timeout)
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length is 0 or > MaximumTextCharacters)
        {
            throw new ArgumentException(
                $"Wait text must contain between 1 and {MaximumTextCharacters} characters.",
                nameof(Text));
        }

        TerminalWaitLimits.ValidateTimeout(Timeout, nameof(Timeout));
        this.Text = Text;
        this.Timeout = Timeout;
    }

    public string Text { get; }

    public TimeSpan Timeout { get; }
}

public sealed record TerminalWaitForChangeInput
{
    public TerminalWaitForChangeInput(long AfterContentRevision, TimeSpan Timeout)
    {
        if (AfterContentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AfterContentRevision));
        }

        TerminalWaitLimits.ValidateTimeout(Timeout, nameof(Timeout));
        this.AfterContentRevision = AfterContentRevision;
        this.Timeout = Timeout;
    }

    public long AfterContentRevision { get; }

    public TimeSpan Timeout { get; }
}

public sealed record TerminalWaitForStableInput
{
    public TerminalWaitForStableInput(TimeSpan StableFor, TimeSpan Timeout)
    {
        TerminalWaitLimits.ValidateTimeout(Timeout, nameof(Timeout));
        if (StableFor <= TimeSpan.Zero || StableFor > TerminalWaitLimits.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StableFor),
                StableFor,
                "The stable interval must be positive and no longer than "
                + $"{TerminalWaitLimits.MaximumTimeout}.");
        }

        this.StableFor = StableFor;
        this.Timeout = Timeout;
    }

    public TimeSpan StableFor { get; }

    public TimeSpan Timeout { get; }
}

internal static class TerminalWaitLimits
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    public static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timeout,
                $"A terminal wait timeout must be positive and no longer than {MaximumTimeout}.");
        }
    }
}
