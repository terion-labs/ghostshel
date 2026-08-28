namespace GhostShell.Core.Tests;

public sealed class BrowserProfileDefinitionTests
{
    [Fact]
    public void DurableProfilePromisesEncryptedChromiumStateBetweenRuns()
    {
        var profile = new BrowserProfileDefinition(
            new BrowserProfileId("browser.work"),
            BrowserProfileDefinition.CurrentSchemaVersion,
            "Work",
            BrowserProfilePersistence.DurableMetadata,
            BrowserProfilePrivacyPolicy.Strict);

        Assert.Equal(
            BrowserWebContentRetention.EncryptedBetweenRuns,
            profile.Privacy.WebContent);
        Assert.Equal(
            BrowserPermissionRetention.DenyAll,
            profile.Privacy.Permissions);
        Assert.Equal(BrowserActivityRetention.DoNotRecord, profile.Privacy.History);
        Assert.Equal(BrowserActivityRetention.DoNotRecord, profile.Privacy.Downloads);
    }

    [Fact]
    public void HttpAuthenticationNormalizesAndBoundsTheExactChallengeTarget()
    {
        var authentication = new BrowserHttpAuthentication(
            "Example.COM.",
            8443,
            " Protected ",
            BrowserAuthenticationScheme.Basic,
            " operator ",
            new SecretRef("secret.browser.password"));

        Assert.Equal("example.com", authentication.Host);
        Assert.Equal("Protected", authentication.Realm);
        Assert.Equal("operator", authentication.Username);
        Assert.Throws<ArgumentException>(() => new BrowserHttpAuthentication(
            "example.com",
            null,
            new string('r', BrowserHttpAuthentication.MaximumRealmLength + 1),
            BrowserAuthenticationScheme.Basic,
            "operator",
            new SecretRef("secret.browser.password")));
    }
}
