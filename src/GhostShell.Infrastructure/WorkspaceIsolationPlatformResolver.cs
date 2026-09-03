using System.Runtime.InteropServices;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed class WorkspaceIsolationPlatformResolver
{
    public const WorkspaceIsolationCapability AppleContainerCapabilities =
        WorkspaceIsolationCapability.PersistentRootFileSystem
        | WorkspaceIsolationCapability.DedicatedKernel
        | WorkspaceIsolationCapability.DedicatedNetworkNamespace
        | WorkspaceIsolationCapability.HostBindMounts
        | WorkspaceIsolationCapability.StructuredProcessExecution;

    public WorkspaceIsolationPlatformSupport ResolveCurrent() =>
        Resolve(
            ConnectionRuntimeOptions.Detect().Platform,
            RuntimeInformation.OSArchitecture,
            Environment.OSVersion.Version);

    public WorkspaceIsolationPlatformSupport Resolve(
        ConnectionHostPlatform platform,
        Architecture architecture,
        Version operatingSystemVersion)
    {
        ArgumentNullException.ThrowIfNull(operatingSystemVersion);
        return platform switch
        {
            ConnectionHostPlatform.MacOs => ResolveMacOs(architecture, operatingSystemVersion),
            ConnectionHostPlatform.Linux => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.LinuxBackendNotPackaged,
                WorkspaceIsolationPlatformLimitation.LinuxKvmRequired,
                WorkspaceIsolationPlatformLimitation.LinuxHostSharingRuntimeRequired,
            ]),
            ConnectionHostPlatform.Windows => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareVirtualMachine,
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareNetworkNamespace,
            ]),
            ConnectionHostPlatform.Other => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.UnsupportedPlatform,
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
    }

    private static WorkspaceIsolationPlatformSupport ResolveMacOs(
        Architecture architecture,
        Version operatingSystemVersion)
    {
        var limitations = new List<WorkspaceIsolationPlatformLimitation>();
        if (architecture != Architecture.Arm64)
        {
            limitations.Add(WorkspaceIsolationPlatformLimitation.AppleSiliconRequired);
        }

        if (operatingSystemVersion.Major < 26)
        {
            limitations.Add(WorkspaceIsolationPlatformLimitation.MacOs26Required);
        }

        return limitations.Count == 0
            ? new WorkspaceIsolationPlatformSupport.Available(
                WorkspaceIsolationProviderKind.AppleContainer,
                AppleContainerCapabilities)
            : new WorkspaceIsolationPlatformSupport.Unavailable(limitations);
    }
}
