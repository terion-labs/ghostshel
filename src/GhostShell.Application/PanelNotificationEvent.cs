namespace GhostShell.Application;

/// <summary>What a panel asked for attention with.</summary>
public enum PanelNotificationKind
{
    /// <summary>
    /// An explicit desktop notification — OSC 9 or OSC 777. Something in the
    /// panel decided this was worth interrupting for, and said what about.
    /// </summary>
    Notification,

    /// <summary>
    /// The terminal bell. It carries no message, so it means only "look at
    /// me" — which is exactly what a long build or an agent waiting on input
    /// tends to send.
    /// </summary>
    Bell,
}

/// <summary>
/// A panel asking to be noticed.
///
/// Deliberately not a <see cref="PanelSessionEvent"/>: those describe where a
/// session is in its lifecycle, and every consumer of them treats an event as a
/// state change. This is neither a state nor a change to one — it is a moment,
/// and a consumer that misses it has missed it.
/// </summary>
/// <param name="Sequence">
/// Monotonic per session, so a watcher that reconnects can say what it has
/// already seen.
/// </param>
/// <param name="Title">
/// What the notification called itself. Empty when the protocol carried only a
/// body, and always empty for a bell.
/// </param>
/// <param name="Body">The message, empty for a bell.</param>
public sealed record PanelNotificationEvent(
    long Sequence,
    PanelNotificationKind Kind,
    string Title,
    string Body,
    DateTimeOffset TimestampUtc);
