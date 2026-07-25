namespace GhostShell.Infrastructure;

internal enum SshKnownHostWriteResult
{
    Stored,
    AlreadyCurrent,
    ChangedSinceReview,
}
