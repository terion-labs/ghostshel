using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal static class ConnectionProbeErrorMapper
{
    public static ConnectionRuntimeError Map(
        ConnectionProbeResult result,
        ConnectionKind kind)
    {
        if (result.Outcome == ConnectionProbeOutcome.Cancelled)
        {
            return Error(ConnectionRuntimeErrorCode.Cancelled);
        }

        if (result.Outcome == ConnectionProbeOutcome.TimedOut)
        {
            return Error(ConnectionRuntimeErrorCode.Timeout);
        }

        if (result.Outcome == ConnectionProbeOutcome.StartFailed)
        {
            return Error(result.StartFailure switch
            {
                ConnectionProbeStartFailure.NotFound => ConnectionRuntimeErrorCode.RuntimeMissing,
                ConnectionProbeStartFailure.PermissionDenied => ConnectionRuntimeErrorCode.PermissionDenied,
                _ => ConnectionRuntimeErrorCode.ProcessFailed,
            });
        }

        var stderr = result.StandardError;
        if (kind == ConnectionKind.Ssh)
        {
            if (Contains(stderr, "REMOTE HOST IDENTIFICATION HAS CHANGED"))
            {
                return Error(ConnectionRuntimeErrorCode.HostKeyChanged);
            }

            if (Contains(stderr, "Host key verification failed")
                || Contains(stderr, "authenticity of host")
                || Contains(stderr, "No ED25519 host key is known"))
            {
                return Error(ConnectionRuntimeErrorCode.UnknownHostKey);
            }

            if (Contains(stderr, "Permission denied")
                || Contains(stderr, "Authentication failed"))
            {
                return Error(ConnectionRuntimeErrorCode.AuthenticationFailed);
            }
        }

        if (kind == ConnectionKind.Docker)
        {
            if (Contains(stderr, "No such container")
                || Contains(stderr, "is not running"))
            {
                return Error(ConnectionRuntimeErrorCode.ContainerNotFound);
            }

            if (Contains(stderr, "permission denied")
                || Contains(stderr, "access is denied"))
            {
                return Error(ConnectionRuntimeErrorCode.PermissionDenied);
            }
        }

        if (kind == ConnectionKind.Wsl
            && (Contains(stderr, "There is no distribution with the supplied name")
                || Contains(stderr, "WSL_E_DISTRO_NOT_FOUND")))
        {
            return Error(ConnectionRuntimeErrorCode.DistributionNotFound);
        }

        if (Contains(stderr, "permission denied")
            || Contains(stderr, "access is denied"))
        {
            return Error(ConnectionRuntimeErrorCode.PermissionDenied);
        }

        if (Contains(stderr, "timed out"))
        {
            return Error(ConnectionRuntimeErrorCode.Timeout);
        }

        if (Contains(stderr, "No route to host")
            || Contains(stderr, "Network is unreachable")
            || Contains(stderr, "Could not resolve hostname")
            || Contains(stderr, "Connection refused")
            || Contains(stderr, "Cannot connect to the Docker daemon")
            || Contains(stderr, "error during connect")
            || Contains(stderr, "connection failed"))
        {
            return Error(ConnectionRuntimeErrorCode.Offline);
        }

        return Error(ConnectionRuntimeErrorCode.ProcessFailed);
    }

    private static bool Contains(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static ConnectionRuntimeError Error(ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeError.Create(code);
}
