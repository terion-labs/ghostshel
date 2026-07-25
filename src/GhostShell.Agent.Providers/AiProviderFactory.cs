using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Providers;

/// <summary>
/// Creates native, request-scoped provider adapters. The returned adapters have no terminal or
/// session-host authority; they can only translate bounded model streams into inert agent events.
/// </summary>
public sealed class AiProviderFactory : IDisposable
{
    private readonly AiProviderRuntimeLimits _limits;
    private readonly AiProviderHttpTransport _transport;
    private readonly AiProviderModelDiscovery _modelDiscovery;
    private bool _disposed;

    public AiProviderFactory(
        ISecretVault secretVault,
        AiProviderRuntimeLimits? limits = null)
        : this(secretVault, handler: null, limits)
    {
    }

    internal AiProviderFactory(
        ISecretVault secretVault,
        HttpMessageHandler? handler,
        AiProviderRuntimeLimits? limits = null)
    {
        _limits = limits ?? AiProviderRuntimeLimits.Default;
        _transport = new AiProviderHttpTransport(
            secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
            handler);
        _modelDiscovery = new AiProviderModelDiscovery(_transport, _limits);
    }

    public IAgentProvider Create(
        AiProviderProfile profile,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!profile.IsEnabled)
        {
            throw new ArgumentException(
                "The AI-provider profile is disabled.",
                nameof(profile));
        }

        var selectedModel = ValidateModel(model ?? profile.DefaultModel);
        return profile.ProviderKind switch
        {
            AiProviderKind.Anthropic => new AnthropicAgentProvider(
                profile,
                selectedModel,
                _transport,
                _limits),
            AiProviderKind.OpenAi or AiProviderKind.OpenAiCompatible =>
                new OpenAiCompatibleAgentProvider(
                    profile,
                    selectedModel,
                    _transport,
                    _limits),
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.ProviderKind,
                "The AI-provider kind is unsupported."),
        };
    }

    public ValueTask<IReadOnlyList<AiProviderModelDescriptor>> ListModelsAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _modelDiscovery.ListAsync(profile, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }

    private static string ValidateModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var normalized = model.Trim();
        if (normalized.Length > AiProviderProfile.MaximumModelIdLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The model identifier must be a bounded printable value.",
                nameof(model));
        }

        return normalized;
    }
}
