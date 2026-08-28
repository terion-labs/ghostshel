namespace GhostShell.Application;

public sealed record BrowserAuthenticationChallenge(
    bool IsProxy,
    string Host,
    int Port,
    string Realm,
    string Scheme);

public sealed record BrowserAuthenticationCredentials(
    string Username,
    string Password);

/// <summary>
/// Resolves one profile-scoped HTTP challenge at the native browser boundary.
/// Implementations return null for every challenge outside the exact profile
/// binding and must never log credential material.
/// </summary>
public interface IBrowserProfileAuthenticationResolver
{
    ValueTask<BrowserAuthenticationCredentials?> ResolveAsync(
        BrowserProfileBinding profile,
        BrowserAuthenticationChallenge challenge,
        CancellationToken cancellationToken);
}
