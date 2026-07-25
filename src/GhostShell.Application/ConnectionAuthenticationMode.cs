namespace GhostShell.Application;

public enum ConnectionAuthenticationMode
{
    None,
    SshAgent,
    Password,
    PrivateKey,
    PrivateKeyWithPassphrase,
}
