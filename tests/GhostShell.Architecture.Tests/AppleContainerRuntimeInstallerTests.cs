using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class AppleContainerRuntimeInstallerTests
{
    [Fact]
    public void Installation_opens_Apples_official_latest_release()
    {
        Uri? opened = null;
        var installer = new AppleContainerRuntimeInstaller(address =>
        {
            opened = address;
            return true;
        });

        Assert.Equal("Apple container", installer.RuntimeDisplayName);

        var result = installer.BeginInstallation();

        Assert.True(result.Started);
        Assert.Null(result.Error);
        Assert.Equal(AppleContainerRuntimeInstaller.OfficialReleasePage, opened);
        Assert.Equal("github.com", opened?.Host);
        Assert.Equal("/apple/container/releases/latest", opened?.AbsolutePath);
    }

    [Fact]
    public void Installation_reports_when_the_system_cannot_open_the_page()
    {
        var installer = new AppleContainerRuntimeInstaller(_ => false);

        var result = installer.BeginInstallation();

        Assert.False(result.Started);
        Assert.Contains("could not open", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
