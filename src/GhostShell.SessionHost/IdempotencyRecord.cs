namespace GhostShell.SessionHost;

internal sealed record IdempotencyRecord(string Fingerprint, object Result);
