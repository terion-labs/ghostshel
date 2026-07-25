using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface ISshHostKeyScanner
{
    ValueTask<ConnectionRuntimeResult<SshHostKeyCandidate>> ScanAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
