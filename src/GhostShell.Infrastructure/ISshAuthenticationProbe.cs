using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface ISshAuthenticationProbe
{
    ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> AuthenticateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
