namespace GhostShell.Application;

/// <summary>
/// A stable, non-sensitive failure suitable for presentation, audit classification, and transport.
/// </summary>
public sealed record ConnectionRuntimeError(
    ConnectionRuntimeErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable,
    ConnectionRecoveryAction RecoveryAction)
{
    public static ConnectionRuntimeError Create(ConnectionRuntimeErrorCode code) => code switch
    {
        ConnectionRuntimeErrorCode.InvalidProfile =>
            New(code, "connection_invalid_profile", "The connection profile is invalid.", false, ConnectionRecoveryAction.EditProfile),
        ConnectionRuntimeErrorCode.AdapterUnavailable =>
            New(code, "connection_adapter_unavailable", "No adapter supports this connection type.", false, ConnectionRecoveryAction.None),
        ConnectionRuntimeErrorCode.RuntimeMissing =>
            New(code, "connection_runtime_missing", "The required connection runtime is not installed or executable.", false, ConnectionRecoveryAction.InstallRuntime),
        ConnectionRuntimeErrorCode.UnsupportedPlatform =>
            New(code, "connection_platform_unsupported", "This connection type is not supported on the current platform.", false, ConnectionRecoveryAction.None),
        ConnectionRuntimeErrorCode.SecretVaultUnavailable =>
            New(code, "connection_secret_vault_unavailable", "The credential vault is unavailable.", true, ConnectionRecoveryAction.UnlockSecretVault),
        ConnectionRuntimeErrorCode.SecretNotFound =>
            New(code, "connection_secret_not_found", "A required connection credential is missing.", false, ConnectionRecoveryAction.ProvideAuthentication),
        ConnectionRuntimeErrorCode.SecretAccessDenied =>
            New(code, "connection_secret_access_denied", "Access to a required connection credential was denied.", false, ConnectionRecoveryAction.ProvideAuthentication),
        ConnectionRuntimeErrorCode.SecretInvalid =>
            New(code, "connection_secret_invalid", "A required connection credential is invalid.", false, ConnectionRecoveryAction.ProvideAuthentication),
        ConnectionRuntimeErrorCode.SecretVaultFailure =>
            New(code, "connection_secret_vault_failure", "The credential vault could not complete the request.", true, ConnectionRecoveryAction.Retry),
        ConnectionRuntimeErrorCode.AuthenticationRequired =>
            New(code, "connection_authentication_required", "Connection authentication requires user interaction.", false, ConnectionRecoveryAction.ProvideAuthentication),
        ConnectionRuntimeErrorCode.AuthenticationFailed =>
            New(code, "connection_authentication_failed", "Connection authentication failed.", false, ConnectionRecoveryAction.ProvideAuthentication),
        ConnectionRuntimeErrorCode.UnknownHostKey =>
            New(code, "connection_host_key_unknown", "The remote host key is not trusted.", false, ConnectionRecoveryAction.ReviewHostKey),
        ConnectionRuntimeErrorCode.HostKeyChanged =>
            New(code, "connection_host_key_changed", "The remote host key changed.", false, ConnectionRecoveryAction.ReviewHostKey),
        ConnectionRuntimeErrorCode.HostKeyReviewExpired =>
            New(code, "connection_host_key_review_expired", "The host-key review expired.", true, ConnectionRecoveryAction.ReviewHostKey),
        ConnectionRuntimeErrorCode.PermissionDenied =>
            New(code, "connection_permission_denied", "The connection runtime was denied permission.", false, ConnectionRecoveryAction.GrantPermission),
        ConnectionRuntimeErrorCode.Timeout =>
            New(code, "connection_timeout", "The connection attempt timed out.", true, ConnectionRecoveryAction.Reconnect),
        ConnectionRuntimeErrorCode.Offline =>
            New(code, "connection_offline", "The connection endpoint is offline or unreachable.", true, ConnectionRecoveryAction.Reconnect),
        ConnectionRuntimeErrorCode.ContainerNotFound =>
            New(code, "connection_container_not_found", "The selected container does not exist or is not running.", false, ConnectionRecoveryAction.SelectContainer),
        ConnectionRuntimeErrorCode.DistributionNotFound =>
            New(code, "connection_distribution_not_found", "The selected WSL distribution is not installed.", false, ConnectionRecoveryAction.SelectDistribution),
        ConnectionRuntimeErrorCode.Cancelled =>
            New(code, "connection_cancelled", "The connection operation was cancelled.", false, ConnectionRecoveryAction.None),
        ConnectionRuntimeErrorCode.ProcessFailed =>
            New(code, "connection_process_failed", "The connection runtime failed.", true, ConnectionRecoveryAction.Retry),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    private static ConnectionRuntimeError New(
        ConnectionRuntimeErrorCode code,
        string stableCode,
        string message,
        bool retryable,
        ConnectionRecoveryAction recoveryAction) =>
        new(code, stableCode, message, retryable, recoveryAction);
}
