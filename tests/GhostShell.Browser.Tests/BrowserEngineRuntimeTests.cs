using Exclr8Cef;

namespace GhostShell.Browser.Tests;

public sealed class BrowserEngineRuntimeTests
{
    [Fact]
    public void ExactPinnedRuntimeVersionIsAccepted()
    {
        BrowserEngineRuntime.ValidateVersions(
            new CefVersions(
                "0.8.0-ghostshell.4",
                "150.0.9",
                "150.0.7871.46"));
    }

    [Theory]
    [InlineData("149.0.0", "150.0.7871.46")]
    [InlineData("150.0.9", "151.0.0.0")]
    public void RuntimeVersionMismatchFailsClosed(
        string cefVersion,
        string chromiumVersion)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BrowserEngineRuntime.ValidateVersions(
                new CefVersions(
                    "0.8.0-ghostshell.4",
                    cefVersion,
                    chromiumVersion)));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public void UnpatchedShimVersionFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BrowserEngineRuntime.ValidateVersions(
                new CefVersions(
                    "0.8.0",
                    "150.0.9",
                    "150.0.7871.46")));
    }

    [Fact]
    public void RuntimeOptionsNormalizePaths()
    {
        var options = new BrowserEngineRuntimeOptions(
            Path.Combine("relative", "profile"),
            Path.Combine("relative", "logs", "cef.log"),
            "1.0.0");

        Assert.True(Path.IsPathFullyQualified(options.ProfileDirectory));
        Assert.True(Path.IsPathFullyQualified(options.LogFilePath));
        Assert.Equal("1.0.0", options.ProductVersion);
    }

    [Fact]
    public void LegacySharedProfileMovesToTheExactRequestContextCachePath()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "Default");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Cookies"), "existing-cookie-db");
        try
        {
            BrowserEngineRuntime.PrepareProfileLayout(root);

            var current = Path.Combine(root, "profiles", "global", "local");
            Assert.False(Directory.Exists(legacy));
            Assert.Equal(
                "existing-cookie-db",
                File.ReadAllText(Path.Combine(current, "Cookies")));
            Assert.False(Directory.Exists(Path.Combine(current, "Default")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
