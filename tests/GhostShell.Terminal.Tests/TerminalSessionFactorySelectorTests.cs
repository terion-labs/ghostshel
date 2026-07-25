namespace GhostShell.Terminal.Tests;

public sealed class TerminalSessionFactorySelectorTests
{
    [Fact]
    public void Macos_selects_ghostty()
    {
        Assert.IsType<GhosttyTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.MacOs));
    }

    [Fact]
    public void Windows_and_linux_select_the_portable_backend()
    {
        Assert.IsType<PortableTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Windows));
        Assert.IsType<PortableTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Linux));
    }

    [Fact]
    public void Unsupported_platform_is_explicit()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Unsupported));
    }
}
