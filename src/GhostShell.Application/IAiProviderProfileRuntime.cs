using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Projects durable AI-provider definitions into bounded connectivity and model-discovery results.
/// HTTP clients, provider payloads, headers, and credential material remain behind this seam.
/// </summary>
public interface IAiProviderProfileRuntime : IDisposable
{
    event EventHandler? ProfilesChanged;

    IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; }

    IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics { get; }

    ValueTask<AiProviderTestResult> TestAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken);

    ValueTask ReloadAsync(CancellationToken cancellationToken);
}

public enum AiProviderRuntimeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum AiProviderRuntimeErrorCode
{
    InvalidConfiguration,
    CredentialUnavailable,
    AuthenticationFailed,
    AccessDenied,
    ModelUnavailable,
    RateLimited,
    QuotaExceeded,
    ProviderUnavailable,
    ProtocolError,
    ResponseTooLarge,
    Timeout,
    Cancelled,
}

public sealed record AiProviderProfileDescriptor(
    AiProviderProfileId Id,
    string Name,
    AiProviderKind ProviderKind,
    Uri Endpoint,
    string DefaultModel,
    int Order,
    bool IsEnabled,
    bool RequiresCredential);

public sealed record AiProviderModelDescriptor
{
    public AiProviderModelDescriptor(string id, string displayName)
    {
        Id = RequireBounded(id, nameof(id));
        DisplayName = RequireBounded(displayName, nameof(displayName));
    }

    public string Id { get; }

    public string DisplayName { get; }

    private static string RequireBounded(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > AiProviderProfile.MaximumModelIdLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The model identifier must be a bounded printable string.",
                parameterName);
        }

        return normalized;
    }
}

public sealed record AiProviderRuntimeDiagnostic(
    AiProviderProfileId? ProfileId,
    AiProviderRuntimeDiagnosticSeverity Severity,
    string Code,
    string Message);

public sealed record AiProviderTestResult(
    bool IsSuccess,
    string Code,
    string Message,
    IReadOnlyList<AiProviderModelDescriptor> Models,
    AiProviderRuntimeErrorCode? ErrorCode = null,
    TimeSpan? RetryAfter = null);
