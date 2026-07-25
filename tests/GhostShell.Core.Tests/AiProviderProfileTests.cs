using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class AiProviderProfileTests
{
    [Fact]
    public void ApiKeyIsPersistedOnlyAsAnOpaqueSecretReference()
    {
        var secret = new SecretRef("vault-ai-openai");
        var profile = CreateProfile(
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1"),
            new AiProviderAuthentication.ApiKey(secret));

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<AiProviderProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(secret, Assert.IsType<AiProviderAuthentication.ApiKey>(
            restored.Authentication).Secret);
        Assert.Equal(new Uri("https://api.openai.com/v1/"), restored.Endpoint);
        Assert.DoesNotContain("secretValue", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://api.openai.com/v1/")]
    [InlineData("https://key@example.test/v1/")]
    [InlineData("https://example.test/v1/?tenant=secret")]
    [InlineData("file:///tmp/provider")]
    public void UnsafeProviderEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri(endpoint),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key"))));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1/")]
    [InlineData("http://127.0.0.1:1234/v1/")]
    [InlineData("http://[::1]:11434/v1/")]
    public void LoopbackProvidersMayExplicitlyUseNoAuthentication(string endpoint)
    {
        var profile = CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri(endpoint),
            new AiProviderAuthentication.None());

        Assert.IsType<AiProviderAuthentication.None>(profile.Authentication);
    }

    [Fact]
    public void RemoteProvidersCannotDisableAuthentication()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAiCompatible,
            new Uri("https://gateway.example.test/v1/"),
            new AiProviderAuthentication.None()));
    }

    [Fact]
    public void IdentityModelAndOrderingAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderProfile(
            new AiProviderProfileId("provider"),
            schemaVersion: 2,
            "Provider",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            "gpt",
            order: 0));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            defaultModel: "gpt\ninjected"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderProfile(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(new SecretRef("provider-key")),
            "gpt",
            order: AiProviderProfile.MaximumOrder + 1));
    }

    private static AiProviderProfile CreateProfile(
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel = "model") =>
        new(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            providerKind,
            endpoint,
            authentication,
            defaultModel,
            order: 0);
}
