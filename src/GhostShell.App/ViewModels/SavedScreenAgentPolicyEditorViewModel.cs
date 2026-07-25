using System.Collections.Immutable;
using System.ComponentModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Edits one optional durable screen-policy override. The available modes omit
/// YOLO because that authority exists only as a confirmed live-run overlay.
/// </summary>
public sealed class SavedScreenAgentPolicyEditorViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<PermissionOption> DurablePermissionOptions =
        Array.AsReadOnly(
        [
            new PermissionOption(AgentPermission.Off),
            new PermissionOption(AgentPermission.Ask),
            new PermissionOption(AgentPermission.Auto),
        ]);

    private readonly IReadOnlyList<CapabilityEditorViewModel> _capabilities;
    private readonly bool _requiresAvailableProvider;
    private bool _isEnabled;
    private string _provider;
    private string _model;
    private ProviderOption? _selectedProvider;
    private bool _disposed;

    public SavedScreenAgentPolicyEditorViewModel(
        AgentPolicy? policy,
        IReadOnlyList<AiProviderProfileDescriptor>? providerProfiles = null)
    {
        _isEnabled = policy is not null;
        var normalized = AgentPolicyResolver.Resolve(policy ?? AgentPolicy.Default);
        _provider = normalized.Provider;
        _model = normalized.Model;
        _requiresAvailableProvider = providerProfiles is not null;

        var providerOptions = (providerProfiles ?? [])
            .OrderBy(profile => profile.Order)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id.Value, StringComparer.Ordinal)
            .Select(profile => new ProviderOption(
                profile.Id,
                profile.Name,
                profile.DefaultModel,
                profile.IsEnabled,
                IsAvailable: true))
            .ToList();
        _selectedProvider = providerOptions.SingleOrDefault(option =>
            string.Equals(option.Id.Value, normalized.Provider, StringComparison.Ordinal));
        if (policy is not null && _selectedProvider is null && _requiresAvailableProvider)
        {
            _selectedProvider = new ProviderOption(
                new AiProviderProfileId(normalized.Provider),
                normalized.Provider,
                normalized.Model,
                IsEnabled: false,
                IsAvailable: false);
            providerOptions.Add(_selectedProvider);
        }
        else if (policy is null && _requiresAvailableProvider)
        {
            _selectedProvider = providerOptions.FirstOrDefault(option => option.IsSelectable);
            if (_selectedProvider is { } selected)
            {
                _provider = selected.Id.Value;
                _model = selected.DefaultModel;
            }
            else
            {
                _provider = string.Empty;
                _model = string.Empty;
            }
        }

        ProviderOptions = providerOptions.AsReadOnly();
        _capabilities = Array.AsReadOnly(
            AgentPolicy.Capabilities
                .Select(capability => new CapabilityEditorViewModel(
                    capability,
                    normalized.GetPermission(capability),
                    DurablePermissionOptions))
                .ToArray());
        foreach (var capability in _capabilities)
        {
            capability.PropertyChanged += OnCapabilityChanged;
        }
    }

    public event EventHandler? Changed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                if (_requiresAvailableProvider)
                {
                    _selectedProvider = ProviderOptions.SingleOrDefault(option =>
                        string.Equals(
                            option.Id.Value,
                            value.Trim(),
                            StringComparison.Ordinal));
                    OnPropertyChanged(nameof(SelectedProvider));
                }

                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IReadOnlyList<ProviderOption> ProviderOptions { get; }

    public ProviderOption? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is not null && !ProviderOptions.Contains(value))
            {
                throw new ArgumentException(
                    "Choose a provider profile offered by this editor.",
                    nameof(value));
            }

            if (!SetProperty(ref _selectedProvider, value))
            {
                return;
            }

            _provider = value?.Id.Value ?? string.Empty;
            _model = value?.DefaultModel ?? string.Empty;
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(IsValid));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<CapabilityEditorViewModel> Capabilities => _capabilities;

    public bool IsValid =>
        !IsEnabled
        || AgentPolicy.IsValidProvider(Provider)
        && AgentPolicy.IsValidModel(Model)
        && (!_requiresAvailableProvider
            || SelectedProvider is { IsSelectable: true } selected
            && string.Equals(
                selected.Id.Value,
                Provider.Trim(),
                StringComparison.Ordinal));

    public AgentPolicy? Build()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (!IsValid)
        {
            throw new ArgumentException(
                "An enabled saved-screen agent policy requires an available, enabled "
                + "provider profile and a bounded model identifier without control characters.");
        }

        var policy = new AgentPolicy(
            Provider.Trim(),
            Model.Trim(),
            Capabilities.ToImmutableDictionary(
                capability => capability.Capability,
                capability => capability.SelectedPermission));
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "Saved-screen agent policy permissions must use Off, Ask, or Auto.");
        }

        return policy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var capability in _capabilities)
        {
            capability.PropertyChanged -= OnCapabilityChanged;
        }

        _disposed = true;
    }

    private void OnCapabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public sealed record PermissionOption(AgentPermission Permission)
    {
        public string DisplayName => AgentPolicyPresentation.PermissionName(Permission);
    }

    public sealed record ProviderOption(
        AiProviderProfileId Id,
        string Name,
        string DefaultModel,
        bool IsEnabled,
        bool IsAvailable)
    {
        public bool IsSelectable => IsAvailable && IsEnabled;

        public string DisplayName => this switch
        {
            { IsAvailable: false } => $"Unavailable · {Id.Value}",
            { IsEnabled: false } => $"Disabled · {Name} · {Id.Value}",
            _ => $"{Name} · {Id.Value}",
        };
    }

    public sealed class CapabilityEditorViewModel : ObservableObject
    {
        private PermissionOption _selectedOption;

        internal CapabilityEditorViewModel(
            AgentCapability capability,
            AgentPermission permission,
            IReadOnlyList<PermissionOption> options)
        {
            Capability = capability;
            Options = options;
            _selectedOption = options.Single(option => option.Permission == permission);
        }

        public AgentCapability Capability { get; }

        public string DisplayName => AgentPolicyPresentation.CapabilityName(Capability);

        public IReadOnlyList<PermissionOption> Options { get; }

        public PermissionOption SelectedOption
        {
            get => _selectedOption;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (!Options.Contains(value))
                {
                    throw new ArgumentException(
                        "The selected permission is not durable.",
                        nameof(value));
                }

                SetProperty(ref _selectedOption, value);
            }
        }

        public AgentPermission SelectedPermission
        {
            get => SelectedOption.Permission;
            set => SelectedOption = Options.Single(option => option.Permission == value);
        }
    }
}
