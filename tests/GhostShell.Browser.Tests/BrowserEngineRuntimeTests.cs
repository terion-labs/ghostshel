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
    public void RuntimeSettingsKeepTheGlobalContextEphemeral()
    {
        var options = new BrowserEngineRuntimeOptions(
            Path.Combine("relative", "profile"),
            Path.Combine("relative", "logs", "cef.log"),
            "1.0.0");

        var settings = BrowserEngineRuntime.CreateSettings(options);

        Assert.Null(settings.CachePath);
        Assert.False(settings.PersistSessionCookies);
        Assert.Equal(options.ProfileDirectory, settings.RootCachePath);
    }

    [Fact]
    public void StartupRemovesTheCompleteLegacyPersistentCefRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "Default");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Cookies"), "existing-cookie-db");
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        File.WriteAllText(Path.Combine(root, "runtime", "Cache"), "cache");
        Directory.CreateDirectory(Path.Combine(root, "profiles", "global", "local"));
        File.WriteAllText(
            Path.Combine(root, "profiles", "global", "local", "History"),
            "history");
        File.WriteAllText(Path.Combine(root, "Local State"), "state");
        try
        {
            BrowserEngineRuntime.PrepareProfileLayout(root);

            Assert.True(Directory.Exists(root));
            Assert.False(Directory.Exists(legacy));
            Assert.False(Directory.Exists(Path.Combine(root, "runtime")));
            Assert.False(Directory.Exists(Path.Combine(root, "profiles")));
            Assert.False(File.Exists(Path.Combine(root, "Local State")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LegacyCleanupFailsClosedAtAFileSystemLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "must-survive");
        File.WriteAllText(outsideFile, "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        try
        {
            Assert.Throws<IOException>(() =>
                BrowserEngineRuntime.PrepareProfileLayout(root));

            Assert.True(File.Exists(outsideFile));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }
}
