using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Bootstrap adapter for Apple's installed <c>container</c> CLI. The long-term macOS
/// provider should ship an app-owned Swift helper built on Apple Containerization so the
/// application, rather than a separately installed CLI, owns lifecycle and networking.
/// </summary>
public sealed class AppleContainerWorkspaceIsolationProvider : IWorkspaceIsolationProvider
{
    public const string DefaultContainerExecutablePath = "/usr/local/bin/container";

    public const string DefaultImageReference =
        "docker.io/library/ubuntu@sha256:95fa486768020359141f1318720f43e7982ef926c792891d984aef9aaf05e7ea";

    internal const string LegacyAlpineImageReference =
        "docker.io/library/alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    private const int MaximumCapturedCharacters = 64 * 1024;
    private const int WorkspaceCpuCount = 1;
    private const ulong WorkspaceMemoryBytes = 1024UL * 1024UL * 1024UL;
    private const string WorkspaceMemoryArgument = "1G";
    private const string DefaultGuestWorkingDirectory = "/root";
    private const string KeepAliveExecutable = "/bin/sleep";
    private const string KeepAliveArgument = "infinity";
    private const string SshBootstrapArgumentZero = "ghostshell-ssh";
    private const string SshBootstrapScript =
        "if [ ! -x \"$1\" ]; then export DEBIAN_FRONTEND=noninteractive; "
        + "if command -v apt-get >/dev/null 2>&1; then apt-get update && apt-get install -y --no-install-recommends openssh-client && rm -rf /var/lib/apt/lists/*; "
        + "elif command -v apk >/dev/null 2>&1; then apk add --no-cache openssh-client; "
        + "else echo 'The selected isolate image cannot install OpenSSH.' >&2; exit 127; fi || exit $?; fi; exec \"$@\"";
    private const string OwnershipLabel = "io.ghostshell.workspace";
    private const string SchemaLabel = "io.ghostshell.isolation-schema";
    private const string BaseImageLabel = "io.ghostshell.base-image";
    private const string SchemaVersion = "1";
    private const string SnapshotContainerfile = """
        FROM scratch
        ADD rootfs.tar /
        ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
        """;
    // Apple Container 1.0.0's tagged command reference includes every CLI surface used here:
    // create/mount, start/stop, inspect, and structured exec environment/workdir flags.
    private static readonly Version MinimumRuntimeVersion = new(1, 0, 0);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly AppleContainerCommandRunner _commandRunner;
    private readonly string _containerExecutable;
    private readonly string _imageReference;
    private readonly string _guestShellExecutable;
    private readonly IReadOnlyList<string> _guestShellArguments;
    private readonly string _guestSshExecutable;
    private readonly ConcurrentDictionary<string, ResourceLeaseState> _resources =
        new(StringComparer.Ordinal);

    public AppleContainerWorkspaceIsolationProvider(
        string imageReference = DefaultImageReference,
        string guestShellExecutable = "/bin/sh",
        string guestSshExecutable = "/usr/bin/ssh",
        string containerExecutable = DefaultContainerExecutablePath)
        : this(
            RunCommandAsync,
            imageReference,
            guestShellExecutable,
            ["-l"],
            guestSshExecutable,
            containerExecutable)
    {
    }

    internal AppleContainerWorkspaceIsolationProvider(
        AppleContainerCommandRunner commandRunner,
        string imageReference,
        string guestShellExecutable,
        IReadOnlyList<string> guestShellArguments,
        string guestSshExecutable,
        string containerExecutable)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _imageReference = ValidateText(imageReference, nameof(imageReference));
        _guestShellExecutable = ValidateText(guestShellExecutable, nameof(guestShellExecutable));
        _guestSshExecutable = ValidateText(guestSshExecutable, nameof(guestSshExecutable));
        _containerExecutable = ValidateText(containerExecutable, nameof(containerExecutable));
        ArgumentNullException.ThrowIfNull(guestShellArguments);
        _guestShellArguments = Array.AsReadOnly(guestShellArguments
            .Select(argument => ValidateArgument(argument, nameof(guestShellArguments)))
            .ToArray());
    }

    public WorkspaceIsolationProviderKind Kind => WorkspaceIsolationProviderKind.AppleContainer;

    public WorkspaceIsolationCapability Capabilities =>
        WorkspaceIsolationPlatformResolver.AppleContainerCapabilities;

    public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(request, progress: null, cancellationToken);

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        if (request.Mounts.Any(mount => !CanEncodeMount(mount)))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.PrepareFailed);
        }

        // Apple's CLI bind-mount parser currently accepts directories only.
        // Keep that provider constraint here so a future native helper can add
        // regular-file mounts without narrowing the durable workspace model.
        if (request.Mounts.Any(mount => !Directory.Exists(mount.HostSource)))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.PrepareFailed);
        }

        var resourceName = ResourceName(request.WorkspaceId);
        var resource = _resources.GetOrAdd(
            resourceName,
            static _ => new ResourceLeaseState());
        try
        {
            await resource.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        try
        {
            if (resource.ActiveLeases.Count > 0
                && resource.Mounts is not null
                && (!MountsEqual(resource.Mounts, request.Mounts)
                    || !string.Equals(
                        resource.ImageReference,
                        request.ImageReference,
                        StringComparison.Ordinal)))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
            }

            if (resource.ActiveLeases.Count > 0)
            {
                var activeInspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!activeInspect.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            activeInspect,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                if (!IsExpectedContainer(activeInspect.StandardOutput, request, resourceName))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
                }

                var activeLiveness = await RunAsync(
                        ["exec", resourceName, "/bin/true"],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!activeLiveness.IsSuccess)
                {
                    var restart = await RunAsync(
                            ["start", resourceName],
                            LifecycleTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    activeLiveness = await RunAsync(
                            ["exec", resourceName, "/bin/true"],
                            ProbeTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!activeLiveness.IsSuccess)
                    {
                        var failure = restart.IsSuccess ? activeLiveness : restart;
                        return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                            MapLifecycleFailure(
                                failure,
                                WorkspaceIsolationErrorCode.PrepareFailed));
                    }
                }

                if (!await EnsureGuestRuntimeAsync(resourceName, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        cancellationToken.IsCancellationRequested
                            ? WorkspaceIsolationErrorCode.Cancelled
                            : WorkspaceIsolationErrorCode.PrepareFailed);
                }

                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(
                    AcquireBinding(resource, request, resourceName));
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Checking the Apple container runtime…"));
            var versionResult = await RunAsync(
                ["system", "version", "--format", "json"],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (TryMapCommandFailure(versionResult, out var versionFailure))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(versionFailure);
            }

            if (!TryReadClientVersion(versionResult.StandardOutput, out var runtimeVersion))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.RuntimeUnavailable);
            }

            if (runtimeVersion < MinimumRuntimeVersion)
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.RuntimeVersionTooOld);
            }

            // `system start` is also Apple's idempotent readiness operation. Running it on
            // every cold provider preparation repairs a prior interrupted first-run kernel
            // install, while an already-ready runtime returns immediately.
            progress?.Report(new WorkspaceIsolationProgress(
                "Starting Apple container services and checking its Linux kernel…"));
            var runtimeProgress = progress is null
                ? null
                : new AppleContainerRuntimeProgress(progress);
            var startSystem = await RunAsync(
                ["system", "start", "--enable-kernel-install", "--timeout", "180"],
                timeout: null,
                cancellationToken,
                runtimeProgress)
                .ConfigureAwait(false);
            if (TryMapCommandFailure(startSystem, out var startSystemFailure))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    startSystemFailure);
            }

            var inspect = await RunAsync(
                ["inspect", resourceName],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (!inspect.IsSuccess)
            {
                progress?.Report(new WorkspaceIsolationProgress(
                    "Downloading the workspace image…"));
                var imageProgress = progress is null
                    ? null
                    : new AppleContainerImageProgress(progress);
                var pull = await RunAsync(
                    ["image", "pull", "--progress", "plain", ImageReferenceFor(request)],
                    timeout: null,
                    cancellationToken,
                    imageProgress)
                    .ConfigureAwait(false);
                if (TryMapCommandFailure(pull, out var pullFailure))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(pullFailure);
                }

                progress?.Report(new WorkspaceIsolationProgress(
                    "Creating the persistent workspace isolate…"));
                var create = await RunAsync(
                    CreateArguments(request, resourceName),
                    CreateTimeout,
                    cancellationToken)
                    .ConfigureAwait(false);
                inspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!inspect.IsSuccess)
                {
                    var failure = create.IsSuccess
                        || inspect.Outcome is AppleContainerCommandOutcome.Cancelled
                            or AppleContainerCommandOutcome.TimedOut
                        ? inspect
                        : create;
                    if (create.Outcome != AppleContainerCommandOutcome.StartFailed)
                    {
                        resource.Mounts = request.Mounts;
                        return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                            MapLifecycleFailure(
                                failure,
                                WorkspaceIsolationErrorCode.PrepareFailed),
                            AcquireBinding(resource, request, resourceName));
                    }

                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(failure, WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }

            if (!TryReadExpectedContainerConfiguration(
                    inspect.StandardOutput,
                    resourceName,
                    out var configuredMounts,
                    out var configuredImage,
                    out var configuredBaseImage))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
            }

            if (!ImageConfigurationMatches(
                    request,
                    resourceName,
                    configuredImage,
                    configuredBaseImage))
            {
                var recreated = await RecreateContainerForImageAsync(
                        request,
                        resourceName,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!recreated.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            recreated,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                configuredMounts = request.Mounts;
            }
            else if (!MountsEqual(configuredMounts, request.Mounts))
            {
                var reconfigured = await ReconfigureContainerAsync(
                        request,
                        resourceName,
                        configuredMounts,
                        configuredBaseImage ?? InferBaseImage(configuredImage, resourceName),
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!reconfigured.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            reconfigured,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                inspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!inspect.IsSuccess
                    || !IsExpectedContainer(inspect.StandardOutput, request, resourceName))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            inspect,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Starting the persistent workspace isolate…"));
            var start = await RunAsync(
                ["start", resourceName],
                LifecycleTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            var live = await RunAsync(
                ["exec", resourceName, "/bin/true"],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (!live.IsSuccess)
            {
                var failed = start.IsSuccess ? live : start;
                var error = MapLifecycleFailure(
                    failed,
                    WorkspaceIsolationErrorCode.PrepareFailed);
                resource.Mounts = request.Mounts;
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    error,
                    AcquireBinding(resource, request, resourceName));
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Checking the workspace isolate…"));
            if (!await EnsureGuestRuntimeAsync(resourceName, cancellationToken).ConfigureAwait(false))
            {
                var error = cancellationToken.IsCancellationRequested
                    ? WorkspaceIsolationErrorCode.Cancelled
                    : WorkspaceIsolationErrorCode.PrepareFailed;
                resource.Mounts = request.Mounts;
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    error,
                    AcquireBinding(resource, request, resourceName));
            }

            resource.Mounts = request.Mounts;
            resource.ImageReference = request.ImageReference;
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(
                AcquireBinding(resource, request, resourceName));
        }
        finally
        {
            resource.Gate.Release();
        }
    }

    public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
        WorkspaceIsolationBinding binding,
        WorkspaceIsolationProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        if (binding.Provider != Kind)
        {
            throw new ArgumentException(
                "The workspace isolation binding belongs to another provider.",
                nameof(binding));
        }

        if (!string.Equals(
                binding.ResourceName,
                ResourceName(binding.WorkspaceId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The workspace isolation binding has an invalid resource name.",
                nameof(binding));
        }

        if (request.UsesHostCredentialBroker)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable);
        }

        if (request.ConnectionKind is ConnectionKind.Docker or ConnectionKind.Wsl)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }

        if (!TryMapWorkingDirectory(
                binding.Mounts,
                request.HostWorkingDirectory,
                out var guestWorkingDirectory))
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted);
        }

        string guestExecutable;
        IReadOnlyList<string> guestArguments;
        switch (request.ConnectionKind)
        {
            case ConnectionKind.Local:
                guestExecutable = _guestShellExecutable;
                guestArguments = _guestShellArguments;
                guestWorkingDirectory ??= DefaultGuestWorkingDirectory;
                break;
            case ConnectionKind.Ssh when IsSshExecutable(request.HostExecutable):
                if (!UsesExplicitlyUnverifiedSshPolicy(request.Arguments))
                {
                    return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                        WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable);
                }

                guestExecutable = _guestShellExecutable;
                guestArguments = Array.AsReadOnly(new[]
                {
                    "-c",
                    SshBootstrapScript,
                    SshBootstrapArgumentZero,
                    _guestSshExecutable,
                }.Concat(request.Arguments).ToArray());
                break;
            case ConnectionKind.Ssh:
                return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                    WorkspaceIsolationErrorCode.ExecutableMappingUnavailable);
            default:
                return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                    WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }

        var arguments = new List<string>(
            8 + (request.Environment.Count * 2) + guestArguments.Count)
        {
            "exec",
        };
        if ((request.Mode & WorkspaceProcessMode.Interactive) != 0)
        {
            arguments.Add("--interactive");
        }

        if ((request.Mode & WorkspaceProcessMode.AllocateTerminal) != 0)
        {
            arguments.Add("--tty");
        }

        foreach (var (name, value) in request.Environment.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            arguments.Add("--env");
            arguments.Add($"{name}={value}");
        }

        if (guestWorkingDirectory is not null)
        {
            arguments.Add("--workdir");
            arguments.Add(guestWorkingDirectory);
        }

        arguments.Add(binding.ResourceName);
        arguments.Add(guestExecutable);
        arguments.AddRange(guestArguments);
        return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
            new WorkspaceProcessLaunch(
                _containerExecutable,
                arguments,
                EmptyEnvironment,
                hostWorkingDirectory: null));
    }

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Provider != Kind)
        {
            throw new ArgumentException(
                "The workspace isolation binding belongs to another provider.",
                nameof(binding));
        }

        if (!string.Equals(
                binding.ResourceName,
                ResourceName(binding.WorkspaceId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The workspace isolation binding has an invalid resource name.",
                nameof(binding));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        if (!_resources.TryGetValue(binding.ResourceName, out var resource))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }

        try
        {
            await resource.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        try
        {
            if (!resource.ActiveLeases.Contains(binding.LeaseId))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            if (resource.ActiveLeases.Count > 1)
            {
                resource.ActiveLeases.Remove(binding.LeaseId);
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            var stop = await StopContainerAsync(binding, cancellationToken).ConfigureAwait(false);
            if (stop is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)
            {
                resource.ActiveLeases.Remove(binding.LeaseId);
            }

            return stop;
        }
        finally
        {
            resource.Gate.Release();
        }
    }

    private async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopContainerAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                ["stop", "--time", "5", binding.ResourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }

        var liveness = await RunAsync(
                ["exec", binding.ResourceName, "/bin/true"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (liveness.IsSuccess)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
        }

        var inspect = await RunAsync(
                ["inspect", binding.ResourceName],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspect.IsSuccess)
        {
            return IsStoppedContainer(inspect.StandardOutput, binding.ResourceName)
                ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding)
                : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
        }

        var list = await RunAsync(
                ["list", "--all", "--format", "json"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return list.IsSuccess
               && ConfirmsContainerAbsent(list.StandardOutput, binding.ResourceName)
            ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding)
            : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
    }

    private WorkspaceIsolationBinding AcquireBinding(
        ResourceLeaseState resource,
        WorkspaceIsolationPrepareRequest request,
        string resourceName)
    {
        var leaseId = Guid.NewGuid();
        resource.ActiveLeases.Add(leaseId);
        resource.Mounts = request.Mounts;
        resource.ImageReference = request.ImageReference;
        return new WorkspaceIsolationBinding(
            request.WorkspaceId,
            Kind,
            Capabilities,
            resourceName,
            request.Mounts,
            leaseId,
            request.ImageReference);
    }

    internal static string ResourceName(WorkspaceId workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId.Value));
        return $"ghostshell-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static bool CanEncodeMount(WorkspaceIsolationMount mount) =>
        CanEncodeMountPath(mount.HostSource)
        && CanEncodeMountPath(mount.GuestDestination);

    private static bool CanEncodeMountPath(string path) =>
        !path.Contains(',', StringComparison.Ordinal)
        && !path.Contains('=', StringComparison.Ordinal);

    private static bool IsSshExecutable(string executable) =>
        string.Equals(Path.GetFileName(executable), "ssh", StringComparison.Ordinal);

    private static bool UsesExplicitlyUnverifiedSshPolicy(
        IReadOnlyList<string> arguments) =>
        HasOpenSshOption(arguments, "StrictHostKeyChecking", "no")
        && HasOpenSshOption(arguments, "UserKnownHostsFile", "/dev/null");

    private static bool HasOpenSshOption(
        IReadOnlyList<string> arguments,
        string expectedName,
        string expectedValue)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "-o", StringComparison.Ordinal))
            {
                continue;
            }

            var option = arguments[index + 1];
            var separator = option.IndexOf('=');
            if (separator > 0
                && string.Equals(
                    option[..separator],
                    expectedName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    option[(separator + 1)..].Trim('"'),
                    expectedValue,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMapWorkingDirectory(
        IReadOnlyList<WorkspaceIsolationMount> mounts,
        string? hostWorkingDirectory,
        out string? guestWorkingDirectory)
    {
        guestWorkingDirectory = null;
        if (hostWorkingDirectory is null)
        {
            return true;
        }

        try
        {
            if (!Path.IsPathFullyQualified(hostWorkingDirectory))
            {
                return false;
            }

            var workingDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(hostWorkingDirectory));
            WorkspaceIsolationMount? matchedMount = null;
            string? matchedRelativePath = null;
            foreach (var mount in mounts.OrderByDescending(
                         candidate => candidate.HostSource.Length))
            {
                var relative = Path.GetRelativePath(mount.HostSource, workingDirectory);
                if (string.Equals(relative, "..", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                    || relative.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matchedMount = mount;
                matchedRelativePath = relative;
                break;
            }

            if (matchedMount is null || matchedRelativePath is null)
            {
                return false;
            }

            guestWorkingDirectory = string.Equals(matchedRelativePath, ".", StringComparison.Ordinal)
                ? matchedMount.GuestDestination
                : $"{matchedMount.GuestDestination}/{ToGuestPath(matchedRelativePath)}";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string ToGuestPath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private IReadOnlyList<string> CreateArguments(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        string? imageReference = null,
        string? baseImageReference = null)
    {
        var arguments = new List<string>(22 + (request.Mounts.Count * 2))
        {
            "create",
            "--name",
            resourceName,
            "--label",
            $"{OwnershipLabel}={resourceName}",
            "--label",
            $"{SchemaLabel}={SchemaVersion}",
            "--label",
            $"{BaseImageLabel}={baseImageReference ?? ImageReferenceFor(request)}",
            "--cpus",
            WorkspaceCpuCount.ToString(CultureInfo.InvariantCulture),
            "--memory",
            WorkspaceMemoryArgument,
        };
        foreach (var mount in request.Mounts.OrderBy(
                     candidate => candidate.GuestDestination,
                     StringComparer.Ordinal))
        {
            arguments.Add("--mount");
            arguments.Add(MountArgument(mount));
        }

        arguments.Add("--workdir");
        arguments.Add(DefaultGuestWorkingDirectory);
        arguments.Add("--init");
        arguments.Add(imageReference ?? ImageReferenceFor(request));
        arguments.Add(KeepAliveExecutable);
        arguments.Add(KeepAliveArgument);
        return arguments;
    }

    private async ValueTask<AppleContainerCommandResult> ReconfigureContainerAsync(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        IReadOnlyList<WorkspaceIsolationMount> configuredMounts,
        string configuredBaseImage,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WorkspaceIsolationProgress(
            "Stopping the workspace isolate before applying host mounts…"));
        var stop = await RunAsync(
                ["stop", "--time", "5", resourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stop.IsSuccess)
        {
            var stoppedInspect = await RunAsync(
                    ["inspect", resourceName],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stoppedInspect.IsSuccess
                || !IsStoppedContainer(stoppedInspect.StandardOutput, resourceName))
            {
                return stop;
            }
        }

        var temporaryDirectory = Directory.CreateTempSubdirectory(
            "ghostshell-isolation-reconfigure-");
        try
        {
            var archivePath = Path.Combine(temporaryDirectory.FullName, "rootfs.tar");
            var containerfilePath = Path.Combine(temporaryDirectory.FullName, "Containerfile");

            progress?.Report(new WorkspaceIsolationProgress(
                "Saving installed packages and guest files…"));
            var export = await RunAsync(
                    ["export", "--output", archivePath, resourceName],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!export.IsSuccess)
            {
                return export;
            }

            await File.WriteAllTextAsync(
                    containerfilePath,
                    SnapshotContainerfile,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshotImage = SnapshotImageReference(resourceName);
            progress?.Report(new WorkspaceIsolationProgress(
                "Building the preserved workspace image…"));
            var build = await RunAsync(
                    [
                        "build",
                        "--progress",
                        "plain",
                        "--tag",
                        snapshotImage,
                        temporaryDirectory.FullName,
                    ],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!build.IsSuccess)
            {
                return build;
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Replacing the workspace isolate with the updated host mounts…"));
            var delete = await RunAsync(
                    ["delete", resourceName],
                    LifecycleTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!delete.IsSuccess)
            {
                return delete;
            }

            var create = await RunAsync(
                    CreateArguments(
                        request,
                        resourceName,
                        snapshotImage,
                        configuredBaseImage),
                    CreateTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (create.IsSuccess)
            {
                return create;
            }

            // The snapshot image contains the complete writable filesystem. Restore the
            // previous mount configuration if applying the new configuration fails.
            _ = await RunAsync(
                    CreateArguments(
                        new WorkspaceIsolationPrepareRequest(
                            request.WorkspaceId,
                            configuredMounts,
                            configuredBaseImage),
                        resourceName,
                        snapshotImage,
                        configuredBaseImage),
                    CreateTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return create;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }
        finally
        {
            try
            {
                temporaryDirectory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
            {
                // The isolate has already been rebuilt or left intact. A temporary export
                // cleanup failure must not replace that lifecycle result.
            }
        }
    }

    private static string SnapshotImageReference(string resourceName) =>
        $"{resourceName}-state:latest";

    private string ImageReferenceFor(WorkspaceIsolationPrepareRequest request) =>
        request.ImageReference ?? _imageReference;

    private static string InferBaseImage(string imageReference, string resourceName) =>
        string.Equals(
            imageReference,
            SnapshotImageReference(resourceName),
            StringComparison.Ordinal)
            ? LegacyAlpineImageReference
            : imageReference;

    private async ValueTask<AppleContainerCommandResult> RecreateContainerForImageAsync(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WorkspaceIsolationProgress(
            "Stopping the workspace isolate before changing its runtime image…"));
        var stop = await RunAsync(
                ["stop", "--time", "5", resourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stop.IsSuccess)
        {
            var stoppedInspect = await RunAsync(
                    ["inspect", resourceName],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stoppedInspect.IsSuccess
                || !IsStoppedContainer(stoppedInspect.StandardOutput, resourceName))
            {
                return stop;
            }
        }

        var imageReference = ImageReferenceFor(request);
        progress?.Report(new WorkspaceIsolationProgress(
            "Downloading the selected workspace image…"));
        var imageProgress = progress is null
            ? null
            : new AppleContainerImageProgress(progress);
        var pull = await RunAsync(
                ["image", "pull", "--progress", "plain", imageReference],
                timeout: null,
                cancellationToken,
                imageProgress)
            .ConfigureAwait(false);
        if (!pull.IsSuccess)
        {
            return pull;
        }

        progress?.Report(new WorkspaceIsolationProgress(
            "Rebuilding the workspace isolate with the selected image…"));
        var delete = await RunAsync(
                ["delete", resourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!delete.IsSuccess)
        {
            return delete;
        }

        return await RunAsync(
                CreateArguments(request, resourceName),
                CreateTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string MountArgument(WorkspaceIsolationMount mount) =>
        $"type=bind,source={mount.HostSource},target={mount.GuestDestination}"
        + (mount.IsReadOnly ? ",readonly" : string.Empty);

    private async ValueTask<AppleContainerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        IProgress<string>? outputProgress = null)
    {
        try
        {
            return await _commandRunner(
                new AppleContainerCommand(
                    _containerExecutable,
                    arguments,
                    timeout,
                    outputProgress),
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }
    }

    private async ValueTask<bool> EnsureGuestRuntimeAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var shell = await RunAsync(
                ["exec", resourceName, _guestShellExecutable, "-c", "exit 0"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!shell.IsSuccess)
        {
            return false;
        }

        var persistentRoot = await RunAsync(
                [
                    "exec",
                    resourceName,
                    _guestShellExecutable,
                    "-c",
                    "test -d /root && test -w /root",
                ],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return persistentRoot.IsSuccess;
    }

    private static bool TryMapCommandFailure(
        AppleContainerCommandResult result,
        out WorkspaceIsolationErrorCode error)
    {
        if (result.IsSuccess)
        {
            error = WorkspaceIsolationErrorCode.None;
            return false;
        }

        error = MapLifecycleFailure(result, WorkspaceIsolationErrorCode.RuntimeUnavailable);
        return true;
    }

    private static WorkspaceIsolationErrorCode MapLifecycleFailure(
        AppleContainerCommandResult result,
        WorkspaceIsolationErrorCode fallback) =>
        result.Outcome switch
        {
            AppleContainerCommandOutcome.StartFailed
                when result.StartFailure == AppleContainerCommandStartFailure.NotFound =>
                WorkspaceIsolationErrorCode.RuntimeMissing,
            AppleContainerCommandOutcome.Cancelled => WorkspaceIsolationErrorCode.Cancelled,
            AppleContainerCommandOutcome.TimedOut => WorkspaceIsolationErrorCode.Timeout,
            _ => fallback,
        };

    private static bool TryReadClientVersion(string json, out Version version)
    {
        version = new Version();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("appName", out var appName)
                    || appName.ValueKind != JsonValueKind.String
                    || !string.Equals(appName.GetString(), "container", StringComparison.Ordinal)
                    || !item.TryGetProperty("version", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } versionText)
                {
                    continue;
                }

                var semanticCore = versionText.Split(['-', '+'], 2)[0];
                return Version.TryParse(semanticCore, out version!);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsExpectedContainer(
        string json,
        WorkspaceIsolationPrepareRequest request,
        string resourceName) =>
        TryReadExpectedContainerConfiguration(
            json,
            resourceName,
            out var configuredMounts,
            out var configuredImage,
            out var configuredBaseImage)
        && MountsEqual(configuredMounts, request.Mounts)
        && ImageConfigurationMatches(
            request,
            resourceName,
            configuredImage,
            configuredBaseImage);

    private bool TryReadExpectedContainerMounts(
        string json,
        string resourceName,
        out IReadOnlyList<WorkspaceIsolationMount> configuredMounts) =>
        TryReadExpectedContainerConfiguration(
            json,
            resourceName,
            out configuredMounts,
            out _,
            out _);

    private static bool TryReadExpectedContainerConfiguration(
        string json,
        string resourceName,
        out IReadOnlyList<WorkspaceIsolationMount> configuredMounts,
        out string configuredImage,
        out string? configuredBaseImage)
    {
        configuredMounts = [];
        configuredImage = string.Empty;
        configuredBaseImage = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var snapshot = document.RootElement[0];
            if (!snapshot.TryGetProperty("configuration", out var configuration)
                || !configuration.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), resourceName, StringComparison.Ordinal)
                || !configuration.TryGetProperty("labels", out var labels)
                || !HasLabel(labels, OwnershipLabel, resourceName)
                || !HasLabel(labels, SchemaLabel, SchemaVersion)
                || !configuration.TryGetProperty("image", out var image)
                || !image.TryGetProperty("reference", out var imageReference)
                || imageReference.ValueKind != JsonValueKind.String
                || !configuration.TryGetProperty("resources", out var resources)
                || !resources.TryGetProperty("cpus", out var cpus)
                || !cpus.TryGetInt32(out var cpuCount)
                || cpuCount != WorkspaceCpuCount
                || !resources.TryGetProperty("memoryInBytes", out var memory)
                || !memory.TryGetUInt64(out var memoryBytes)
                || memoryBytes != WorkspaceMemoryBytes
                || !configuration.TryGetProperty("ssh", out var ssh)
                || ssh.ValueKind is not JsonValueKind.False
                || !configuration.TryGetProperty("useInit", out var useInit)
                || useInit.ValueKind is not JsonValueKind.True
                || !configuration.TryGetProperty("mounts", out var mounts)
                || mounts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            configuredImage = imageReference.GetString()!;
            if (labels.TryGetProperty(BaseImageLabel, out var baseImage)
                && baseImage.ValueKind == JsonValueKind.String)
            {
                configuredBaseImage = baseImage.GetString();
            }

            var mountsFromRuntime = new List<WorkspaceIsolationMount>();
            foreach (var mount in mounts.EnumerateArray())
            {
                if (!TryReadMount(mount, out var configured))
                {
                    return false;
                }

                mountsFromRuntime.Add(new WorkspaceIsolationMount(
                    configured.HostSource,
                    configured.GuestDestination,
                    configured.IsReadOnly));
            }

            configuredMounts = mountsFromRuntime.AsReadOnly();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private bool ImageConfigurationMatches(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        string configuredImage,
        string? configuredBaseImage)
    {
        if (request.ImageReference is null)
        {
            // Definitions saved before image selection existed inherit an already-created
            // environment. New environments still use the Ubuntu default.
            return string.Equals(configuredImage, _imageReference, StringComparison.Ordinal)
                || string.Equals(
                    configuredImage,
                    LegacyAlpineImageReference,
                    StringComparison.Ordinal)
                || (string.Equals(
                        configuredImage,
                        SnapshotImageReference(resourceName),
                        StringComparison.Ordinal)
                    && (configuredBaseImage is null
                        || string.Equals(
                            configuredBaseImage,
                            _imageReference,
                            StringComparison.Ordinal)
                        || string.Equals(
                            configuredBaseImage,
                            LegacyAlpineImageReference,
                            StringComparison.Ordinal)));
        }

        return string.Equals(
                configuredBaseImage,
                request.ImageReference,
                StringComparison.Ordinal)
            && (string.Equals(
                    configuredImage,
                    request.ImageReference,
                    StringComparison.Ordinal)
                || string.Equals(
                    configuredImage,
                    SnapshotImageReference(resourceName),
                    StringComparison.Ordinal));
    }

    private static bool HasLabel(JsonElement labels, string name, string expectedValue) =>
        labels.ValueKind == JsonValueKind.Object
        && labels.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal);

    private static bool IsStoppedContainer(string json, string resourceName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var snapshot = document.RootElement[0];
            return snapshot.TryGetProperty("configuration", out var configuration)
                   && configuration.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                   && string.Equals(id.GetString(), resourceName, StringComparison.Ordinal)
                   && snapshot.TryGetProperty("status", out var status)
                   && IsStoppedStatus(status);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ConfirmsContainerAbsent(string json, string expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var container in document.RootElement.EnumerateArray())
            {
                if (container.ValueKind != JsonValueKind.Object
                    || !container.TryGetProperty("configuration", out var configuration)
                    || configuration.ValueKind != JsonValueKind.Object
                    || !configuration.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } resourceName)
                {
                    return false;
                }

                if (string.Equals(resourceName, expectedName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStoppedStatus(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.String)
        {
            return string.Equals(status.GetString(), "stopped", StringComparison.Ordinal);
        }

        return status.ValueKind == JsonValueKind.Object
               && status.TryGetProperty("state", out var state)
               && state.ValueKind == JsonValueKind.String
               && string.Equals(state.GetString(), "stopped", StringComparison.Ordinal);
    }

    private static bool TryReadMount(JsonElement mount, out InspectedMount configured)
    {
        configured = default;
        if (!mount.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.String
            || source.GetString() is not { } hostSource
            || !mount.TryGetProperty("destination", out var destination)
            || destination.ValueKind != JsonValueKind.String
            || destination.GetString() is not { } guestDestination)
        {
            return false;
        }

        var isReadOnly = false;
        if (mount.TryGetProperty("options", out var options))
        {
            if (options.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                isReadOnly |= option.GetString() is "ro" or "readonly";
            }
        }

        configured = new InspectedMount(hostSource, guestDestination, isReadOnly);
        return true;
    }

    private static bool MountsEqual(
        IReadOnlyList<WorkspaceIsolationMount> left,
        IReadOnlyList<WorkspaceIsolationMount> right) =>
        left.Count == right.Count
        && left.All(expected => right.Any(actual => actual == expected));

    private static string ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace isolation values cannot contain NUL characters.", parameterName);
        }

        return value;
    }

    private static string ValidateArgument(string? value, string parameterName)
    {
        if (value is null || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Workspace isolation arguments cannot be null or contain NUL characters.",
                parameterName);
        }

        return value;
    }

    private static async ValueTask<AppleContainerCommandResult> RunCommandAsync(
        AppleContainerCommand command,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }

        using var process = CreateProcess(command);
        try
        {
            if (!process.Start())
            {
                return AppleContainerCommandResult.StartFailed(
                    AppleContainerCommandStartFailure.Unknown);
            }
        }
        catch (Win32Exception exception)
        {
            return AppleContainerCommandResult.StartFailed(exception.NativeErrorCode switch
            {
                2 or 3 => AppleContainerCommandStartFailure.NotFound,
                5 or 13 => AppleContainerCommandStartFailure.PermissionDenied,
                _ => AppleContainerCommandStartFailure.Unknown,
            });
        }
        catch (FileNotFoundException)
        {
            return AppleContainerCommandResult.StartFailed(
                AppleContainerCommandStartFailure.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return AppleContainerCommandResult.StartFailed(
                AppleContainerCommandStartFailure.PermissionDenied);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryKill(process);
            return AppleContainerCommandResult.ExecutionFailed;
        }

        var stdoutTask = Task.FromResult(string.Empty);
        var stderrTask = Task.FromResult(string.Empty);
        try
        {
            using var timeout = command.Timeout is { } timeoutValue
                ? new CancellationTokenSource(timeoutValue)
                : null;
            using var linked = timeout is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
            stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                linked.Token,
                command.OutputProgress);
            stderrTask = ReadBoundedAsync(
                process.StandardError,
                linked.Token,
                command.OutputProgress);
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stdoutTask,
                    stderrTask)
                .ConfigureAwait(false);
            return AppleContainerCommandResult.Exited(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAfterCancellationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? AppleContainerCommandResult.Cancelled
                : command.Timeout is not null
                    ? AppleContainerCommandResult.TimedOut
                    : AppleContainerCommandResult.ExecutionFailed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryKill(process);
            await AwaitDrainAfterCancellationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return AppleContainerCommandResult.ExecutionFailed;
        }
    }

    private static Process CreateProcess(AppleContainerCommand command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var result = new char[MaximumCapturedCharacters];
        var scratch = new char[2048];
        var written = 0;
        while (true)
        {
            var read = await reader.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new string(result, 0, written);
            }

            progress?.Report(new string(scratch, 0, read));

            var remaining = result.Length - written;
            if (remaining > 0)
            {
                var copy = Math.Min(read, remaining);
                scratch.AsSpan(0, copy).CopyTo(result.AsSpan(written));
                written += copy;
            }
        }
    }

    private static async Task AwaitDrainAfterCancellationAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The cancellation outcome is represented by the command result.
        }
        catch (IOException)
        {
            // Closing redirected streams is expected after forced process teardown.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The command outcome already records the execution failure. Stream-drain cleanup
            // must not allow a secondary reader fault to escape that typed boundary.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
        catch (Win32Exception)
        {
            // The operating system completed or denied teardown; the result stays typed.
        }
    }

    private sealed class AppleContainerRuntimeProgress(
        IProgress<WorkspaceIsolationProgress> progress) : IProgress<string>
    {
        private const int MaximumTailCharacters = 4096;
        private readonly object _gate = new();
        private string _tail = string.Empty;
        private string? _lastStatus;

        public void Report(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                _tail += value;
                if (_tail.Length > MaximumTailCharacters)
                {
                    _tail = _tail[^MaximumTailCharacters..];
                }

                var status = CurrentStatus(_tail);
                if (status is null || string.Equals(status, _lastStatus, StringComparison.Ordinal))
                {
                    return;
                }

                _lastStatus = status;
                progress.Report(new WorkspaceIsolationProgress(status));
            }
        }

        private static string? CurrentStatus(string output)
        {
            var bestIndex = -1;
            string? status = null;
            Consider(
                output,
                "Launching container-apiserver",
                "Starting Apple container services…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Testing access to container-apiserver",
                "Waiting for Apple container services…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Verifying machine API server is running",
                "Checking the Apple container machine service…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Installing kernel",
                "Downloading and installing the Apple container Linux kernel…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Verifying kernel archive",
                "Verifying the Apple container Linux kernel…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Unpacking kernel",
                "Unpacking the Apple container Linux kernel…",
                ref bestIndex,
                ref status);

            const string downloadMarker = "Downloading kernel ";
            var downloadIndex = output.LastIndexOf(downloadMarker, StringComparison.Ordinal);
            if (downloadIndex <= bestIndex)
            {
                return status;
            }

            var valueStart = downloadIndex + downloadMarker.Length;
            var valueEnd = valueStart;
            while (valueEnd < output.Length && char.IsAsciiDigit(output[valueEnd]))
            {
                valueEnd++;
            }

            return valueEnd > valueStart
                   && valueEnd < output.Length
                   && output[valueEnd] == '%'
                ? $"Downloading the Apple container Linux kernel… {output[valueStart..valueEnd]}%"
                : "Downloading the Apple container Linux kernel…";
        }

        private static void Consider(
            string output,
            string marker,
            string candidate,
            ref int bestIndex,
            ref string? status)
        {
            var index = output.LastIndexOf(marker, StringComparison.Ordinal);
            if (index <= bestIndex)
            {
                return;
            }

            bestIndex = index;
            status = candidate;
        }
    }

    private sealed class AppleContainerImageProgress(
        IProgress<WorkspaceIsolationProgress> progress) : IProgress<string>
    {
        private const int MaximumTailCharacters = 1024;
        private readonly object _gate = new();
        private string _tail = string.Empty;
        private string? _lastStatus;

        public void Report(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                _tail += value;
                if (_tail.Length > MaximumTailCharacters)
                {
                    _tail = _tail[^MaximumTailCharacters..];
                }

                var status = CurrentStatus(_tail);
                if (string.Equals(status, _lastStatus, StringComparison.Ordinal))
                {
                    return;
                }

                _lastStatus = status;
                progress.Report(new WorkspaceIsolationProgress(status));
            }
        }

        private static string CurrentStatus(string output)
        {
            const string marker = "Fetching image ";
            var markerIndex = output.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return "Downloading the workspace image…";
            }

            var valueStart = markerIndex + marker.Length;
            var valueEnd = valueStart;
            while (valueEnd < output.Length && char.IsAsciiDigit(output[valueEnd]))
            {
                valueEnd++;
            }

            return valueEnd > valueStart
                   && valueEnd < output.Length
                   && output[valueEnd] == '%'
                ? $"Downloading the workspace image… {output[valueStart..valueEnd]}%"
                : "Downloading the workspace image…";
        }
    }

    private sealed class ResourceLeaseState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public HashSet<Guid> ActiveLeases { get; } = [];

        public IReadOnlyList<WorkspaceIsolationMount>? Mounts { get; set; }

        public string? ImageReference { get; set; }
    }

    private readonly record struct InspectedMount(
        string HostSource,
        string GuestDestination,
        bool IsReadOnly);
}

internal delegate ValueTask<AppleContainerCommandResult> AppleContainerCommandRunner(
    AppleContainerCommand command,
    CancellationToken cancellationToken);

internal sealed record AppleContainerCommand
{
    public AppleContainerCommand(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        IProgress<string>? outputProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, null);
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        OutputProgress = outputProgress;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan? Timeout { get; }

    public IProgress<string>? OutputProgress { get; }
}

internal enum AppleContainerCommandOutcome
{
    Exited = 1,
    StartFailed = 2,
    TimedOut = 3,
    Cancelled = 4,
    ExecutionFailed = 5,
}

internal enum AppleContainerCommandStartFailure
{
    None = 0,
    NotFound = 1,
    PermissionDenied = 2,
    Unknown = 3,
}

internal sealed record AppleContainerCommandResult
{
    private AppleContainerCommandResult(
        AppleContainerCommandOutcome outcome,
        int? exitCode,
        string standardOutput,
        string standardError,
        AppleContainerCommandStartFailure startFailure)
    {
        Outcome = outcome;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        StartFailure = startFailure;
    }

    public AppleContainerCommandOutcome Outcome { get; }

    public int? ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public AppleContainerCommandStartFailure StartFailure { get; }

    public bool IsSuccess => Outcome == AppleContainerCommandOutcome.Exited && ExitCode == 0;

    public static AppleContainerCommandResult Cancelled { get; } =
        new(AppleContainerCommandOutcome.Cancelled, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult TimedOut { get; } =
        new(AppleContainerCommandOutcome.TimedOut, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult ExecutionFailed { get; } =
        new(AppleContainerCommandOutcome.ExecutionFailed, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult Exited(
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        new(AppleContainerCommandOutcome.Exited, exitCode, standardOutput, standardError,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult StartFailed(
        AppleContainerCommandStartFailure failure) =>
        new(AppleContainerCommandOutcome.StartFailed, null, string.Empty, string.Empty, failure);
}
