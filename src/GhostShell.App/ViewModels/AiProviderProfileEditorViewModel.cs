using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record AiProviderProfileSaveRequest(
    AiProviderProfile Profile,
    long? ExpectedRevision);

public sealed record AiProviderSecretOption(
    SecretRef? Reference,
    string Label,
    string Kind,
    bool IsAvailable)
{
    public string DisplayName => Reference is null
        ? Label
        : IsAvailable
            ? $"{Label} · {Kind}"
            : $"Missing · {Reference.Value.Value}";
}

public sealed class AiProviderProfileEditorViewModel : ObservableObject
{
    private readonly IAiProviderProfileRuntime _runtime;
    private readonly AiProviderProfileId _id;
    private readonly SecretRef _pendingSecretReference = SecretRef.New();
    private readonly int _schemaVersion;
    private string _name = string.Empty;
    private AiProviderKind _kind = AiProviderKind.OpenAi;
    private string _endpoint = AiProviderProfile
        .DefaultEndpoint(AiProviderKind.OpenAi)
        .AbsoluteUri;
    private string _defaultModel = "gpt-5";
    private int _order;
    private bool _isEnabled = true;
    private bool _useNoAuthentication;
    private AiProviderSecretOption? _selectedCredential;
    private bool _isTesting;
    private string _testStatus = "Not tested";
    private string _testDetail =
        "Provider tests resolve the scoped OS-vault credential and perform one bounded model listing.";
    private IReadOnlyList<AiProviderModelDescriptor> _models = [];

    public AiProviderProfileEditorViewModel(
        IAiProviderProfileRuntime runtime,
        IReadOnlyList<SecretMetadataViewModel> secrets,
        AiProviderProfile? existing = null,
        long? expectedRevision = null,
        int suggestedOrder = 0)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(secrets);
        ExpectedRevision = expectedRevision;
        _id = existing?.Id ?? AiProviderProfileId.New();
        _schemaVersion = existing?.SchemaVersion ?? AiProviderProfile.CurrentSchemaVersion;
        ProviderKinds = Enum.GetValues<AiProviderKind>();
        SecretOptions = BuildSecretOptions(secrets, existing);
        _selectedCredential = SecretOptions[0];
        _order = suggestedOrder;

        if (existing is null)
        {
            return;
        }

        _name = existing.Name;
        _kind = existing.ProviderKind;
        _endpoint = existing.Endpoint.AbsoluteUri;
        _defaultModel = existing.DefaultModel;
        _order = existing.Order;
        _isEnabled = existing.IsEnabled;
        _useNoAuthentication = existing.Authentication is AiProviderAuthentication.None;
        if (existing.Authentication is AiProviderAuthentication.ApiKey apiKey)
        {
            _selectedCredential = SecretOptions.First(option =>
                option.Reference == apiKey.Secret);
        }
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit AI provider" : "New AI provider";

    public string ProfileId => _id.Value;

    public IReadOnlyList<AiProviderKind> ProviderKinds { get; }

    public IReadOnlyList<AiProviderSecretOption> SecretOptions { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public AiProviderKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value))
            {
                return;
            }

            Endpoint = AiProviderProfile.DefaultEndpoint(value).AbsoluteUri;
            DefaultModel = value switch
            {
                AiProviderKind.Anthropic => "claude-sonnet-4-5",
                AiProviderKind.OpenAi => "gpt-5",
                AiProviderKind.OpenAiCompatible => "local-model",
                _ => string.Empty,
            };
            UseNoAuthentication = value == AiProviderKind.OpenAiCompatible;
            OnPropertyChanged(nameof(CanDisableAuthentication));
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public string DefaultModel
    {
        get => _defaultModel;
        set => SetProperty(ref _defaultModel, value);
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool UseNoAuthentication
    {
        get => _useNoAuthentication;
        set
        {
            if (SetProperty(ref _useNoAuthentication, value))
            {
                OnPropertyChanged(nameof(UsesCredential));
            }
        }
    }

    public bool CanDisableAuthentication => Kind == AiProviderKind.OpenAiCompatible;

    public bool UsesCredential => !UseNoAuthentication;

    public AiProviderSecretOption? SelectedCredential
    {
        get => _selectedCredential;
        set => SetProperty(ref _selectedCredential, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    public string TestStatus
    {
        get => _testStatus;
        private set => SetProperty(ref _testStatus, value);
    }

    public string TestDetail
    {
        get => _testDetail;
        private set => SetProperty(ref _testDetail, value);
    }

    public IReadOnlyList<AiProviderModelDescriptor> Models
    {
        get => _models;
        private set => SetProperty(ref _models, value);
    }

    public AiProviderProfileSaveRequest CreateSaveRequest() =>
        new(BuildProfile(), ExpectedRevision);

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
        {
            return;
        }

        AiProviderProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            TestStatus = "Validation failed";
            TestDetail = exception.Message;
            return;
        }

        IsTesting = true;
        TestStatus = "Testing provider";
        TestDetail = "Resolving the scoped credential and listing bounded model metadata…";
        try
        {
            var result = await _runtime.TestAsync(profile, cancellationToken);
            Models = result.Models;
            TestStatus = result.IsSuccess ? "Provider connected" : "Test failed";
            TestDetail = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatus = "Test cancelled";
            TestDetail = "The provider test was cancelled.";
        }
        catch (Exception)
        {
            TestStatus = "Test failed";
            TestDetail = "The provider runtime could not complete the bounded connectivity test.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private AiProviderProfile BuildProfile()
    {
        if (!Uri.TryCreate(Required(Endpoint, "Endpoint"), UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URI.");
        }

        if (UseNoAuthentication && !CanDisableAuthentication)
        {
            throw new ArgumentException(
                "Only an OpenAI-compatible loopback provider can disable authentication.");
        }

        var authentication = UseNoAuthentication
            ? (AiProviderAuthentication)new AiProviderAuthentication.None()
            : new AiProviderAuthentication.ApiKey(
                SelectedCredential?.Reference ?? _pendingSecretReference);
        return new AiProviderProfile(
            _id,
            _schemaVersion,
            Required(Name, "Provider name"),
            Kind,
            endpoint,
            authentication,
            Required(DefaultModel, "Default model"),
            Order,
            IsEnabled);
    }

    private IReadOnlyList<AiProviderSecretOption> BuildSecretOptions(
        IReadOnlyList<SecretMetadataViewModel> secrets,
        AiProviderProfile? existing)
    {
        var options = new List<AiProviderSecretOption>
        {
            new(
                null,
                existing is null
                    ? "Create credential slot when saved"
                    : "Replace with a new credential slot",
                "API KEY",
                true),
        };
        options.AddRange(secrets
            .Where(item => item.SecretScope.Kind == SecretScopeKind.AiProvider
                && item.SecretScope.OwnerId == _id.Value)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AiProviderSecretOption(
                item.Reference,
                item.Label,
                item.Kind,
                true)));
        if (existing?.Authentication is AiProviderAuthentication.ApiKey apiKey
            && options.All(item => item.Reference != apiKey.Secret))
        {
            options.Add(new AiProviderSecretOption(
                apiKey.Secret,
                "Missing credential",
                "UNAVAILABLE",
                false));
        }

        return options.AsReadOnly();
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} is required.");
        }

        return value.Trim();
    }
}
