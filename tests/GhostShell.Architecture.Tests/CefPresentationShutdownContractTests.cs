namespace GhostShell.Architecture.Tests;

public sealed class CefPresentationShutdownContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Desktop_tears_down_presentation_before_finalization_and_cef_shutdown()
    {
        var program = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "GhostShell.Desktop", "Program.cs"));

        var exitRegistration = RequiredIndexOf(
            program,
            "lifetime.Exit += (_, _) =>");
        var lifetimeStart = RequiredIndexOf(program, "lifetime.Start(args)");
        var mainThreadFallback = RequiredIndexOf(
            program,
            "TeardownPresentationOrReport(mainWindowViewModel);",
            lifetimeStart);
        var finalization = RequiredIndexOf(
            program,
            "FinalizeAsync(services).GetAwaiter().GetResult();",
            mainThreadFallback);
        var finallyBlock = RequiredIndexOf(
            program,
            "instanceCoordinator.StopAcceptingActivations();",
            finalization);
        var failureFallback = RequiredIndexOf(
            program,
            "TeardownPresentationOrReport(mainWindowViewModel);",
            finallyBlock);
        var cefShutdown = RequiredIndexOf(
            program,
            "BrowserEngineRuntime.Shutdown()",
            failureFallback);

        Assert.True(exitRegistration < lifetimeStart);
        Assert.True(lifetimeStart < mainThreadFallback);
        Assert.True(mainThreadFallback < finalization);
        Assert.True(finalization < failureFallback);
        Assert.True(failureFallback < cefShutdown);
    }

    private static int RequiredIndexOf(
        string source,
        string value,
        int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected Program.cs to contain '{value}'.");
        return index;
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

        throw new DirectoryNotFoundException("Could not find the GhostSHELL repository root.");
    }
}
