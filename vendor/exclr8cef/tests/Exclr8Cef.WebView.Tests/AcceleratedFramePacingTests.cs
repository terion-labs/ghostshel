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
    [InlineData(true, true, Cef.OffscreenFlags.SharedTexture)]
    [InlineData(false, true, Cef.OffscreenFlags.None)]
    [InlineData(true, false, Cef.OffscreenFlags.None)]
    public void BackgroundBrowserHonorsAcceleratedRenderingPreference(
        bool preferred,
        bool supported,
        Cef.OffscreenFlags expected)
    {
        var flags = WebView.BackgroundBrowserCreationFlags(
            preferred,
            supported);

        Assert.Equal(expected, flags);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void AcceleratedBackgroundBrowserCanInitializePresentationLater(
        bool initializationStarted,
        bool browserCreated,
        bool browserAccelerated,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebView.CanInitializeAcceleratedPresentation(
                initializationStarted,
                browserCreated,
                browserAccelerated));
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

    [Theory]
    [InlineData(1600, 900, 2.0, 800, 450)]
    [InlineData(800, 450, 1.0, 800, 450)]
    [InlineData(600, 300, 0.0, 600, 300)]
    public void AcceleratedFrameKeepsItsPixelCorrectLogicalSize(
        int physicalWidth,
        int physicalHeight,
        double renderScale,
        double expectedWidth,
        double expectedHeight)
    {
        var size = WebView.AcceleratedFrameVisualSize(
            physicalWidth,
            physicalHeight,
            renderScale);

        Assert.Equal(expectedWidth, size.X);
        Assert.Equal(expectedHeight, size.Y);
    }

    [Theory]
    [InlineData(1600, 900, 800, 450, 2.0, true)]
    [InlineData(1601, 901, 800, 450, 2.0, true)]
    [InlineData(1400, 900, 800, 450, 2.0, false)]
    [InlineData(1600, 700, 800, 450, 2.0, false)]
    public void AcceleratedPresentationRejectsFramesFromOlderResizeRequests(
        int physicalWidth,
        int physicalHeight,
        int logicalWidth,
        int logicalHeight,
        double renderScale,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebView.AcceleratedFrameMatchesView(
                physicalWidth,
                physicalHeight,
                logicalWidth,
                logicalHeight,
                renderScale));
    }
}
