using System.ComponentModel;
using System.Diagnostics;
using GhostShell.Application;

namespace GhostShell.Desktop;

internal sealed class AppleContainerRuntimeInstaller(
    Func<Uri, bool>? launch = null) : IWorkspaceIsolationRuntimeInstaller
{
    internal static readonly Uri OfficialReleasePage = new(
        "https://github.com/apple/container/releases/latest",
        UriKind.Absolute);

    private readonly Func<Uri, bool> _launch = launch ?? Launch;

    public string RuntimeDisplayName => "Apple container";

    public WorkspaceIsolationRuntimeInstallResult BeginInstallation()
    {
        try
        {
            return _launch(OfficialReleasePage)
                ? WorkspaceIsolationRuntimeInstallResult.Success()
                : WorkspaceIsolationRuntimeInstallResult.Failure(
                    "GhostSHELL could not open Apple's container installer page.");
        }
        catch (Exception exception) when (exception is
            InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return WorkspaceIsolationRuntimeInstallResult.Failure(
                "GhostSHELL could not open Apple's container installer page.");
        }
    }

    private static bool Launch(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return process is not null;
    }
}
