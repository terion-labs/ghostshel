namespace GhostShell.Application;

public enum ConnectionPlanWarning
{
    HostKeyVerificationDisabled,
    SecretBrokerRequired,
    RemoteEnvironmentRequiresServerAcceptance,
    SshStartupDirectoryRequiresPosixShell,
}
