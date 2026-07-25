namespace GhostShell.Application;

/// <summary>
/// Trusted browser-document identity projected by SessionHost. This is
/// comparison evidence for agent context and never reusable authority.
/// </summary>
public sealed record BrowserSessionMetadata
{
    public BrowserSessionMetadata(
        BrowserNavigationOrigin origin,
        long documentRevision)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        DocumentRevision = documentRevision;
    }

    public BrowserNavigationOrigin Origin { get; }

    public long DocumentRevision { get; }

    public static BrowserSessionMetadata FromState(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserSessionMetadata(
            BrowserNavigationOrigin.FromAddress(state.Address),
            state.DocumentRevision);
    }
}
