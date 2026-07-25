using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class AiProviderProfileEditorViewModelTests
{
    [Fact]
    public void New_openai_profile_uses_an_opaque_repairable_credential_slot()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            suggestedOrder: 3)
        {
            Name = "OpenAI",
        };

        var request = editor.CreateSaveRequest();

        Assert.Null(request.ExpectedRevision);
        Assert.Equal(AiProviderKind.OpenAi, request.Profile.ProviderKind);
        Assert.Equal(3, request.Profile.Order);
        Assert.Equal(new Uri("https://api.openai.com/v1/"), request.Profile.Endpoint);
        var authentication = Assert.IsType<AiProviderAuthentication.ApiKey>(
            request.Profile.Authentication);
        Assert.False(string.IsNullOrWhiteSpace(authentication.Secret.Value));
        Assert.DoesNotContain(
            authentication.Secret.Value,
            request.Profile.Name,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compatible_loopback_profile_can_explicitly_disable_authentication()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://127.0.0.1:11434/v1/",
            DefaultModel = "local-model",
            UseNoAuthentication = true,
        };

        var request = editor.CreateSaveRequest();

        Assert.IsType<AiProviderAuthentication.None>(request.Profile.Authentication);
    }

    [Fact]
    public void Remote_profile_cannot_disable_authentication()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Gateway",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "https://gateway.example.test/v1/",
            DefaultModel = "model",
            UseNoAuthentication = true,
        };

        var exception = Assert.Throws<ArgumentException>(editor.CreateSaveRequest);

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_existing_credential_reference_is_preserved_for_repair()
    {
        using var runtime = new StubRuntime();
        var reference = new SecretRef("missing-provider-key");
        var profile = Profile(
            new AiProviderAuthentication.ApiKey(reference),
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"));
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            profile,
            expectedRevision: 7);

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(
            reference,
            Assert.IsType<AiProviderAuthentication.ApiKey>(
                request.Profile.Authentication).Secret);
        Assert.Contains(
            editor.SecretOptions,
            option => option.Reference == reference && !option.IsAvailable);
    }

    [Fact]
    public async Task Test_projects_bounded_runtime_result_and_models()
    {
        using var runtime = new StubRuntime
        {
            Result = new AiProviderTestResult(
                true,
                "ai_provider_test_succeeded",
                "Connected.",
                [new AiProviderModelDescriptor("model", "Model")]),
        };
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost:11434/v1/",
            DefaultModel = "model",
            UseNoAuthentication = true,
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Provider connected", editor.TestStatus);
        Assert.Equal("Connected.", editor.TestDetail);
        Assert.Equal("model", Assert.Single(editor.Models).Id);
        Assert.NotNull(runtime.LastProfile);
    }

    [Fact]
    public void Existing_scoped_secret_is_the_only_reusable_option()
    {
        using var runtime = new StubRuntime();
        var own = Secret(
            new SecretRef("own-key"),
            new SecretScope(SecretScopeKind.AiProvider, "provider"));
        var other = Secret(
            new SecretRef("other-key"),
            new SecretScope(SecretScopeKind.AiProvider, "other-provider"));
        var profile = Profile(
            new AiProviderAuthentication.ApiKey(own.Reference),
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"));

        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [own, other],
            profile,
            expectedRevision: 1);

        Assert.Contains(editor.SecretOptions, option => option.Reference == own.Reference);
        Assert.DoesNotContain(editor.SecretOptions, option => option.Reference == other.Reference);
    }

    private static AiProviderProfile Profile(
        AiProviderAuthentication authentication,
        AiProviderKind kind,
        Uri endpoint) =>
        new(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            kind,
            endpoint,
            authentication,
            "model",
            order: 0);

    private static SecretMetadataViewModel Secret(SecretRef reference, SecretScope scope) =>
        new(
            reference,
            "API key",
            "ApiKey",
            scope.Kind.ToString(),
            "Today",
            "Never",
            scope,
            "No saved definition dependencies",
            0);

    private sealed class StubRuntime : IAiProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged;

        public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; set; } = [];

        public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics { get; set; } = [];

        public AiProviderTestResult Result { get; set; } = new(
            false,
            "ai_provider_unavailable",
            "Unavailable.",
            [],
            AiProviderRuntimeErrorCode.ProviderUnavailable);

        public AiProviderProfile? LastProfile { get; private set; }

        public ValueTask<AiProviderTestResult> TestAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProfile = profile;
            return ValueTask.FromResult(Result);
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
