namespace GhostShell.Architecture.Tests;

public sealed class CefAcceleratedPresentationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Mac_display_link_marshals_rate_limited_frames_to_cef_ui_thread()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "vendor",
            "exclr8cef",
            "native",
            "shim",
            "exclr8cef_mac.mm"));
        var callback = Slice(
            source,
            "CVReturn ExternalBeginFrameDisplayLinkCallback(",
            "void StopExternalBeginFrameClock(");
        var task = Slice(
            source,
            "class ExternalBeginFrameTask final",
            "void EnsureAcceleratedCopyDevice()");

        Assert.Contains("CefPostTask(TID_UI, task)", callback);
        Assert.DoesNotContain("GetHost()->SendExternalBeginFrame()", callback);
        Assert.Contains("GetHost()->SendExternalBeginFrame()", task);
        Assert.Contains("std::min(60.0, clock->nominal_hz)", source);
        Assert.Contains("frame_task_pending.compare_exchange_strong", callback);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Expected source to contain '{start}'.");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Expected source to contain '{end}'.");
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the GhostSHELL repository root.");
    }
}
