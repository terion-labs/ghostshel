using System.Collections.Concurrent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>Process-lifetime host-key store suitable for ephemeral profiles and tests.</summary>
public sealed class InMemorySftpKnownHostStore : ISshHostKeyTrustStore
{
    private readonly ConcurrentDictionary<ConnectionId, SshHostKeyCandidate> _candidates = new();

    public SshHostKeyVerification Verify(
        ConnectionId connectionId,
        SshHostKeyPolicy policy,
        SshHostKeyCandidate presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (policy == SshHostKeyPolicy.InsecureIgnore)
        {
            return SshHostKeyVerification.Trusted;
        }

        if (_candidates.TryGetValue(connectionId, out var current))
        {
            return current == presented
                ? SshHostKeyVerification.Trusted
                : SshHostKeyVerification.Changed;
        }

        if (policy != SshHostKeyPolicy.AcceptNew)
        {
            return SshHostKeyVerification.Unknown;
        }

        if (_candidates.TryAdd(connectionId, presented))
        {
            return SshHostKeyVerification.Trusted;
        }

        return _candidates.TryGetValue(connectionId, out current) && current == presented
            ? SshHostKeyVerification.Trusted
            : SshHostKeyVerification.Changed;
    }
}
