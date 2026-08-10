namespace Exclr8Cef.WebView.Tests;

public sealed class AcceleratedFramePacingTests
{
    [Fact]
    public void AcceleratedBrowserUsesHostDisplayLinkedBeginFrames()
    {
        var flags = WebView.BrowserCreationFlags(accelerated: true);

        Assert.True((flags & Cef.OffscreenFlags.SharedTexture) != 0);
        Assert.True((flags & Cef.OffscreenFlags.ExternalBeginFrame) != 0);
    }

    [Fact]
    public void CpuFallbackKeepsCefFrameTimer()
    {
        Assert.Equal(
            Cef.OffscreenFlags.None,
            WebView.BrowserCreationFlags(accelerated: false));
        Assert.Equal(30, WebView.CpuFallbackFrameRate);
    }

    [Fact]
    public void FixedSixtyFpsComparisonKeepsSharedTextureWithoutExternalFrames()
    {
        var flags = WebView.BrowserCreationFlags(
            accelerated: true,
            displayLinked: false);

        Assert.True((flags & Cef.OffscreenFlags.SharedTexture) != 0);
        Assert.True((flags & Cef.OffscreenFlags.ExternalBeginFrame) == 0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("fixed-60", false)]
    [InlineData("display-link", true)]
    [InlineData("DISPLAY-LINK", true)]
    public void DisplayLinkPacingRequiresExplicitOptIn(
        string? requestedMode,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebView.DisplayLinkedFramePacingEnabled(requestedMode));
    }
}
