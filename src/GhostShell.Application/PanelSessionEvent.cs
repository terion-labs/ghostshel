namespace GhostShell.Application;

public sealed record PanelSessionEvent(
    long Sequence,
    SessionLifecycle Lifecycle,
    SessionHealth Health,
    DateTimeOffset TimestampUtc,
    string Detail);
