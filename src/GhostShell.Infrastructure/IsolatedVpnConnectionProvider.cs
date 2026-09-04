using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Attaches one supported VPN client inside a workspace isolate or delegates host
/// placement to an app-scoped userspace transport. Neither path changes host routes
/// or host network interfaces.
/// </summary>
public sealed class IsolatedVpnConnectionProvider : INetworkConnectionProvider
{
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultHealthPollInterval = TimeSpan.FromSeconds(5);
    private const string StageSecretScript = """
        set -eu
        dir=$1
        file=$2
        umask 077
        mkdir -p -- "$dir"
        rm -f -- "$dir/$file"
        cat > "$dir/$file"
        chmod 600 "$dir/$file"
        """;

    private readonly ISecretVault _secretVault;
    private readonly IWorkspaceIsolationProvider? _isolationProvider;
    private readonly IWorkspaceIsolationCommandRunner _commandRunner;
    private readonly TimeSpan _healthPollInterval;
    private readonly IHostUserspaceVpnTransport? _hostTransport;

    public IsolatedVpnConnectionProvider(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IWorkspaceIsolationProvider? isolationProvider)
        : this(
            kind,
            secretVault,
            isolationProvider,
            new WorkspaceIsolationCommandRunner(),
            healthPollInterval: null,
            hostTransport: new HostUserspaceVpnTransport(
                kind,
                secretVault,
                new PathConnectionExecutableLocator()))
    {
    }

    internal IsolatedVpnConnectionProvider(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IWorkspaceIsolationProvider? isolationProvider,
        IWorkspaceIsolationCommandRunner commandRunner,
        TimeSpan? healthPollInterval = null,
        IHostUserspaceVpnTransport? hostTransport = null)
    {
        if (kind is not (
                NetworkConnectionKind.WireGuard
                or NetworkConnectionKind.OpenVpn
                or NetworkConnectionKind.AnyConnect
                or NetworkConnectionKind.Tailscale))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Kind = kind;
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _isolationProvider = isolationProvider;
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _hostTransport = hostTransport;
        _healthPollInterval = healthPollInterval ?? DefaultHealthPollInterval;
        if (_healthPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(healthPollInterval),
                healthPollInterval,
                "The VPN health polling interval must be positive.");
        }
    }

    public NetworkConnectionKind Kind { get; }

    public async ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Connection.ConnectionKind != Kind)
        {
            return Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "vpn_configuration_kind_mismatch",
                $"The selected connection does not contain a {DisplayName} configuration.",
                retryable: false);
        }

        if (request.Placement is WorkspaceNetworkPlacement.HostPlacement)
        {
            if (_hostTransport is null)
            {
                return Fail(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    $"{StablePrefix}_host_userspace_unavailable",
                    $"{DisplayName} does not have an app-scoped userspace transport in this build.",
                    retryable: false);
            }

            return await _hostTransport.ConnectAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Placement is not WorkspaceNetworkPlacement.IsolatedPlacement isolated)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Placement, null);
        }

        if (_isolationProvider is null)
        {
            return Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                $"{StablePrefix}_isolation_runtime_unavailable",
                $"{DisplayName} cannot start because the workspace isolation runtime is unavailable.",
                retryable: false);
        }

        const WorkspaceIsolationCapability requiredCapabilities =
            WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.StructuredProcessExecution;
        if ((isolated.Binding.Capabilities & requiredCapabilities) != requiredCapabilities)
        {
            return Fail(
                NetworkConnectionErrorCode.RouteUnavailable,
                $"{StablePrefix}_dedicated_network_unavailable",
                $"{DisplayName} requires an isolate with its own network namespace.",
                retryable: false);
        }

        var invalid = ValidateConfiguration(request.Connection.Configuration);
        if (invalid is not null)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(invalid);
        }

        var preflight = IsolatedVpnConnectionPlans.Preflight(Kind);
        progress?.Report(new NetworkConnectionProgress(
            $"Checking {preflight.DisplayName} in the workspace environment…"));
        var preflightRun = await RunScriptAsync(
                isolated.Binding,
                preflight.Script,
                [],
                ReadOnlyMemory<byte>.Empty,
                PreflightTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (preflightRun is NetworkConnectionResult<WorkspaceIsolationCommandResult>.Failure
            preflightFailure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(preflightFailure.Error);
        }

        var preflightResult =
            ((NetworkConnectionResult<WorkspaceIsolationCommandResult>.Success)preflightRun).Value;
        if (preflightResult.ExitCode != 0)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                MapPreflightFailure(preflight, preflightResult.ExitCode));
        }

        progress?.Report(new NetworkConnectionProgress(
            $"Preparing {preflight.DisplayName} configuration…"));
        var planResult = await ResolvePlanAsync(request.Connection, cancellationToken)
            .ConfigureAwait(false);
        if (planResult is NetworkConnectionResult<ResolvedVpnPlan>.Failure planFailure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(planFailure.Error);
        }

        using var resolved =
            ((NetworkConnectionResult<ResolvedVpnPlan>.Success)planResult).Value;
        var plan = resolved.Plan;
        var cleanupLaunch = CreateLaunch(
            isolated.Binding,
            plan.CleanupScript,
            plan.CleanupArguments);
        if (cleanupLaunch is NetworkConnectionResult<WorkspaceProcessLaunch>.Failure cleanupFailure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(cleanupFailure.Error);
        }

        var cleanup = ((NetworkConnectionResult<WorkspaceProcessLaunch>.Success)cleanupLaunch).Value;
        var healthLaunch = CreateLaunch(
            isolated.Binding,
            plan.HealthScript,
            plan.HealthArguments);
        if (healthLaunch is NetworkConnectionResult<WorkspaceProcessLaunch>.Failure healthFailure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(healthFailure.Error);
        }

        var health = ((NetworkConnectionResult<WorkspaceProcessLaunch>.Success)healthLaunch).Value;
        var staleCleanup = await RunLaunchAsync(
                cleanup,
                ReadOnlyMemory<byte>.Empty,
                CleanupTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (staleCleanup is NetworkConnectionResult<WorkspaceIsolationCommandResult>.Failure)
        {
            return StaleCleanupFailed();
        }

        if (((NetworkConnectionResult<WorkspaceIsolationCommandResult>.Success)staleCleanup)
            .Value.ExitCode != 0)
        {
            return StaleCleanupFailed();
        }

        foreach (var secretFile in plan.SecretFiles)
        {
            var stage = await RunScriptAsync(
                    isolated.Binding,
                    StageSecretScript,
                    [DirectoryFor(request.Connection.Id), secretFile.FileName],
                    secretFile.Content,
                    PreflightTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stage is NetworkConnectionResult<WorkspaceIsolationCommandResult>.Failure
                stageFailure)
            {
                if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
                {
                    return CleanupFailed();
                }

                return NetworkConnectionResult<INetworkConnectionSession>.Fail(stageFailure.Error);
            }

            if (((NetworkConnectionResult<WorkspaceIsolationCommandResult>.Success)stage)
                .Value.ExitCode != 0)
            {
                if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
                {
                    return CleanupFailed();
                }

                return Fail(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    $"{StablePrefix}_configuration_stage_failed",
                    $"The {DisplayName} configuration could not be prepared inside the workspace environment.",
                    retryable: true);
            }
        }

        progress?.Report(new NetworkConnectionProgress(
            $"Connecting {preflight.DisplayName} in the workspace environment…"));
        var attach = await RunScriptAsync(
                isolated.Binding,
                plan.AttachScript,
                plan.AttachArguments,
                plan.StandardInput ?? ReadOnlyMemory<byte>.Empty,
                AttachTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (attach is NetworkConnectionResult<WorkspaceIsolationCommandResult>.Failure attachFailure)
        {
            if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
            {
                return CleanupFailed();
            }

            return NetworkConnectionResult<INetworkConnectionSession>.Fail(attachFailure.Error);
        }

        var attachResult =
            ((NetworkConnectionResult<WorkspaceIsolationCommandResult>.Success)attach).Value;
        if (attachResult.ExitCode != 0)
        {
            if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
            {
                return CleanupFailed();
            }

            return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                MapAttachFailure(plan, attachResult));
        }

        progress?.Report(new NetworkConnectionProgress(
            $"Verifying {preflight.DisplayName} workspace reachability…"));
        var initialHealth = await RunLaunchAsync(
                health,
                ReadOnlyMemory<byte>.Empty,
                HealthTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (initialHealth is NetworkConnectionResult<WorkspaceIsolationCommandResult>.Failure
            initialHealthFailure)
        {
            if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
            {
                return CleanupFailed();
            }

            return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                initialHealthFailure.Error);
        }

        var initialHealthResult =
            ((NetworkConnectionResult<WorkspaceIsolationCommandResult>.Success)initialHealth).Value;
        if (initialHealthResult.ExitCode != 0)
        {
            if (!await TryCleanupAsync(cleanup).ConfigureAwait(false))
            {
                return CleanupFailed();
            }

            return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                MapHealthFailure(initialHealthResult.ExitCode));
        }

        progress?.Report(new NetworkConnectionProgress($"{preflight.DisplayName} is connected."));
        return NetworkConnectionResult<INetworkConnectionSession>.Succeed(
            new IsolatedVpnConnectionSession(
                request.Connection.Id,
                preflight.DisplayName,
                health,
                cleanup,
                _commandRunner,
                _healthPollInterval));
    }

    private string DisplayName => IsolatedVpnConnectionPlans.Preflight(Kind).DisplayName;

    private string StablePrefix => Kind switch
    {
        NetworkConnectionKind.WireGuard => "wireguard",
        NetworkConnectionKind.OpenVpn => "openvpn",
        NetworkConnectionKind.AnyConnect => "anyconnect",
        NetworkConnectionKind.Tailscale => "tailscale",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };

    private NetworkConnectionError? ValidateConfiguration(
        NetworkConnectionConfiguration configuration)
    {
        if (configuration is NetworkConnectionConfiguration.AnyConnect
            { PasswordSecret: null, ClientCertificateSecret: null })
        {
            return Error(
                NetworkConnectionErrorCode.AuthenticationRequired,
                "anyconnect_credentials_required",
                "Cisco AnyConnect requires a password or client certificate for unattended workspace attachment.",
                retryable: false);
        }

        return null;
    }

    private async ValueTask<NetworkConnectionResult<ResolvedVpnPlan>> ResolvePlanAsync(
        NetworkConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var token = TokenFor(profile.Id);
        var directory = DirectoryFor(profile.Id);
        var interfaceName = WorkspaceIsolationNetworkNames.TunnelInterface(profile.Id);
        return profile.Configuration switch
        {
            NetworkConnectionConfiguration.WireGuard wireGuard =>
                await ResolveSingleSecretPlanAsync(
                        profile.Id,
                        wireGuard.ConfigurationSecret,
                        "WireGuard configuration",
                        isCredential: false,
                        value => IsolatedVpnConnectionPlans.WireGuard(
                            directory,
                            interfaceName,
                            value),
                        cancellationToken)
                    .ConfigureAwait(false),
            NetworkConnectionConfiguration.OpenVpn openVpn =>
                await ResolveSingleSecretPlanAsync(
                        profile.Id,
                        openVpn.ConfigurationSecret,
                        "OpenVPN configuration",
                        isCredential: false,
                        value => IsolatedVpnConnectionPlans.OpenVpn(
                            directory,
                            interfaceName,
                            value),
                        cancellationToken)
                    .ConfigureAwait(false),
            NetworkConnectionConfiguration.AnyConnect anyConnect =>
                await ResolveAnyConnectPlanAsync(
                        profile.Id,
                        anyConnect,
                        directory,
                        interfaceName,
                        cancellationToken)
                    .ConfigureAwait(false),
            NetworkConnectionConfiguration.Tailscale tailscale =>
                await ResolveTailscalePlanAsync(
                        profile.Id,
                        tailscale,
                        directory,
                        interfaceName,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => NetworkConnectionResult<ResolvedVpnPlan>.Fail(
                Error(
                    NetworkConnectionErrorCode.InvalidConfiguration,
                    "vpn_configuration_kind_mismatch",
                    $"The selected connection does not contain a {DisplayName} configuration.",
                    retryable: false)),
        };
    }

    private async ValueTask<NetworkConnectionResult<ResolvedVpnPlan>>
        ResolveSingleSecretPlanAsync(
            NetworkConnectionId connectionId,
            SecretRef reference,
            string label,
            bool isCredential,
            Func<byte[], IsolatedVpnConnectionPlan> createPlan,
            CancellationToken cancellationToken)
    {
        var resolved = await ResolveSecretAsync(
                connectionId,
                reference,
                label,
                isCredential,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
        {
            return NetworkConnectionResult<ResolvedVpnPlan>.Fail(failure.Error);
        }

        var value = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        return NetworkConnectionResult<ResolvedVpnPlan>.Succeed(
            new ResolvedVpnPlan(createPlan(value), [value]));
    }

    private async ValueTask<NetworkConnectionResult<ResolvedVpnPlan>> ResolveAnyConnectPlanAsync(
        NetworkConnectionId connectionId,
        NetworkConnectionConfiguration.AnyConnect configuration,
        string directory,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var values = new List<byte[]>(2);
        byte[]? password = null;
        byte[]? certificate = null;
        if (configuration.PasswordSecret is { } passwordReference)
        {
            var result = await ResolveSecretAsync(
                    connectionId,
                    passwordReference,
                    "Cisco AnyConnect password",
                    isCredential: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is NetworkConnectionResult<byte[]>.Failure failure)
            {
                return NetworkConnectionResult<ResolvedVpnPlan>.Fail(failure.Error);
            }

            password = ((NetworkConnectionResult<byte[]>.Success)result).Value;
            values.Add(password);
        }

        if (configuration.ClientCertificateSecret is { } certificateReference)
        {
            var result = await ResolveSecretAsync(
                    connectionId,
                    certificateReference,
                    "Cisco AnyConnect client certificate",
                    isCredential: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is NetworkConnectionResult<byte[]>.Failure failure)
            {
                Clear(values);
                return NetworkConnectionResult<ResolvedVpnPlan>.Fail(failure.Error);
            }

            certificate = ((NetworkConnectionResult<byte[]>.Success)result).Value;
            values.Add(certificate);
        }

        return NetworkConnectionResult<ResolvedVpnPlan>.Succeed(
            new ResolvedVpnPlan(
                IsolatedVpnConnectionPlans.AnyConnect(
                    configuration,
                    directory,
                    interfaceName,
                    password,
                    certificate),
                values));
    }

    private async ValueTask<NetworkConnectionResult<ResolvedVpnPlan>> ResolveTailscalePlanAsync(
        NetworkConnectionId connectionId,
        NetworkConnectionConfiguration.Tailscale configuration,
        string directory,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        if (configuration.AuthKeySecret is not { } reference)
        {
            return NetworkConnectionResult<ResolvedVpnPlan>.Succeed(
                new ResolvedVpnPlan(
                    IsolatedVpnConnectionPlans.Tailscale(
                        configuration,
                        directory,
                        interfaceName,
                        authKey: null),
                    []));
        }

        var resolved = await ResolveSecretAsync(
                connectionId,
                reference,
                "Tailscale authentication key",
                isCredential: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
        {
            return NetworkConnectionResult<ResolvedVpnPlan>.Fail(failure.Error);
        }

        var authKey = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        return NetworkConnectionResult<ResolvedVpnPlan>.Succeed(
            new ResolvedVpnPlan(
                IsolatedVpnConnectionPlans.Tailscale(
                    configuration,
                    directory,
                    interfaceName,
                    authKey),
                [authKey]));
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolveSecretAsync(
        NetworkConnectionId connectionId,
        SecretRef reference,
        string label,
        bool isCredential,
        CancellationToken cancellationToken)
    {
        var targetId = connectionId.Value;
        var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    reference,
                    new SecretScope(SecretScopeKind.NetworkConnection, targetId),
                    new SecretUsePurpose(
                        SecretUseKind.NetworkConnectionAuthentication,
                        targetId)),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            return NetworkConnectionResult<byte[]>.Fail(
                MapSecretFailure(failure.Error, label, isCredential));
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)resolved).Value;
        var value = GC.AllocateUninitializedArray<byte>(material.Length);
        material.CopyTo(value);
        return NetworkConnectionResult<byte[]>.Succeed(value);
    }

    private NetworkConnectionError MapSecretFailure(
        SecretVaultError failure,
        string label,
        bool isCredential) => failure.Code switch
        {
            SecretVaultErrorCode.Cancelled or SecretVaultErrorCode.UserCancelled => Error(
                NetworkConnectionErrorCode.Cancelled,
                $"{StablePrefix}_secret_cancelled",
                $"Access to the {label} was cancelled.",
                retryable: false),
            SecretVaultErrorCode.NotFound or SecretVaultErrorCode.CorruptEntry => Error(
                isCredential
                    ? NetworkConnectionErrorCode.AuthenticationRequired
                    : NetworkConnectionErrorCode.InvalidConfiguration,
                isCredential
                    ? $"{StablePrefix}_credential_missing"
                    : $"{StablePrefix}_configuration_missing",
                $"The {label} is missing or invalid in the secret vault.",
                retryable: false),
            SecretVaultErrorCode.AccessDenied or SecretVaultErrorCode.AuthenticationRequired => Error(
                NetworkConnectionErrorCode.AuthenticationRequired,
                $"{StablePrefix}_secret_access_required",
                $"GhostSHELL cannot access the {label} until secret-vault authentication succeeds.",
                retryable: false),
            _ => Error(
                NetworkConnectionErrorCode.ConnectionFailed,
                $"{StablePrefix}_secret_vault_failed",
                $"The secret vault could not provide the {label}.",
                failure.Retryable),
        };

    private async ValueTask<NetworkConnectionResult<WorkspaceIsolationCommandResult>>
        RunScriptAsync(
            WorkspaceIsolationBinding binding,
            string script,
            IReadOnlyList<string> arguments,
            ReadOnlyMemory<byte> standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var launch = CreateLaunch(binding, script, arguments);
        if (launch is NetworkConnectionResult<WorkspaceProcessLaunch>.Failure failure)
        {
            return NetworkConnectionResult<WorkspaceIsolationCommandResult>.Fail(failure.Error);
        }

        return await RunLaunchAsync(
                ((NetworkConnectionResult<WorkspaceProcessLaunch>.Success)launch).Value,
                standardInput,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private NetworkConnectionResult<WorkspaceProcessLaunch> CreateLaunch(
        WorkspaceIsolationBinding binding,
        string script,
        IReadOnlyList<string> arguments)
    {
        if (_isolationProvider is null)
        {
            return NetworkConnectionResult<WorkspaceProcessLaunch>.Fail(
                Error(
                    NetworkConnectionErrorCode.RuntimeMissing,
                    $"{StablePrefix}_isolation_runtime_unavailable",
                    "The workspace isolation runtime is unavailable.",
                    retryable: false));
        }

        var result = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", script, "ghostshell-network", .. arguments]));
        return result switch
        {
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success success =>
                NetworkConnectionResult<WorkspaceProcessLaunch>.Succeed(success.Value),
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure =>
                NetworkConnectionResult<WorkspaceProcessLaunch>.Fail(
                    Error(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        $"{StablePrefix}_isolation_launch_unavailable",
                        failure.Error.Message,
                        failure.Error.Retryable)),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
    }

    private async ValueTask<NetworkConnectionResult<WorkspaceIsolationCommandResult>> RunLaunchAsync(
        WorkspaceProcessLaunch launch,
        ReadOnlyMemory<byte> standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var result = await _commandRunner.RunAsync(launch, standardInput, deadline.Token)
                .ConfigureAwait(false);
            return NetworkConnectionResult<WorkspaceIsolationCommandResult>.Succeed(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<WorkspaceIsolationCommandResult>.Fail(
                Error(
                    NetworkConnectionErrorCode.Cancelled,
                    $"{StablePrefix}_connection_cancelled",
                    $"Connecting {DisplayName} was cancelled.",
                    retryable: false));
        }
        catch (OperationCanceledException)
        {
            return NetworkConnectionResult<WorkspaceIsolationCommandResult>.Fail(
                Error(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    $"{StablePrefix}_command_timed_out",
                    $"{DisplayName} did not finish the current connection step in time.",
                    retryable: true));
        }
        catch (IOException)
        {
            return NetworkConnectionResult<WorkspaceIsolationCommandResult>.Fail(
                Error(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    $"{StablePrefix}_isolation_command_failed",
                    $"The workspace environment could not run {DisplayName}.",
                    retryable: true));
        }
    }

    private NetworkConnectionError MapPreflightFailure(
        IsolatedVpnPreflight preflight,
        int exitCode) => exitCode switch
        {
            68 => Error(
                NetworkConnectionErrorCode.RuntimeMissing,
                $"{StablePrefix}_health_probe_runtime_missing",
                "Workspace VPN health checks require curl in the workspace environment.",
                retryable: false),
            69 => Error(
                NetworkConnectionErrorCode.RuntimeMissing,
                $"{StablePrefix}_runtime_missing",
                $"{preflight.DisplayName} is not installed in the workspace environment. {preflight.InstallHint}",
                retryable: false),
            77 => Error(
                NetworkConnectionErrorCode.RouteUnavailable,
                $"{StablePrefix}_network_privileges_unavailable",
                $"The workspace user cannot administer its isolated network namespace for {preflight.DisplayName}.",
                retryable: false),
            _ => Error(
                NetworkConnectionErrorCode.ConnectionFailed,
                $"{StablePrefix}_preflight_failed",
                $"The workspace environment could not verify {preflight.DisplayName}.",
                retryable: true),
        };

    private NetworkConnectionError MapHealthFailure(int exitCode) => exitCode switch
    {
        64 => Error(
            NetworkConnectionErrorCode.RouteUnavailable,
            $"{StablePrefix}_full_route_unavailable",
            $"{DisplayName} does not provide a full workspace route.",
            retryable: true),
        65 => Error(
            NetworkConnectionErrorCode.RouteUnavailable,
            $"{StablePrefix}_reachability_failed",
            $"{DisplayName} is connected but cannot carry workspace traffic.",
            retryable: true),
        _ => Error(
            NetworkConnectionErrorCode.ConnectionFailed,
            $"{StablePrefix}_health_check_failed",
            $"{DisplayName} stopped before workspace reachability could be verified.",
            retryable: true),
    };

    private NetworkConnectionError MapAttachFailure(
        IsolatedVpnConnectionPlan plan,
        WorkspaceIsolationCommandResult result)
    {
        var diagnostic = $"{result.StandardError}\n{result.StandardOutput}";
        if (result.ExitCode == 77 || ContainsAuthenticationFailure(diagnostic))
        {
            return Error(
                NetworkConnectionErrorCode.AuthenticationRequired,
                $"{StablePrefix}_authentication_failed",
                $"{plan.DisplayName} rejected the supplied authentication.",
                retryable: false);
        }

        if (result.ExitCode == 64 || ContainsConfigurationFailure(diagnostic))
        {
            return Error(
                NetworkConnectionErrorCode.InvalidConfiguration,
                $"{StablePrefix}_configuration_invalid",
                $"The {plan.DisplayName} configuration is invalid or does not define a usable route.",
                retryable: false);
        }

        if (result.ExitCode == 69)
        {
            return Error(
                NetworkConnectionErrorCode.RuntimeMissing,
                $"{StablePrefix}_runtime_missing",
                $"{plan.DisplayName} is not installed in the workspace environment. {plan.InstallHint}",
                retryable: false);
        }

        return Error(
            NetworkConnectionErrorCode.ConnectionFailed,
            $"{StablePrefix}_connection_failed",
            $"{plan.DisplayName} could not establish a route in the workspace environment.",
            retryable: true);
    }

    private static bool ContainsAuthenticationFailure(string value) =>
        value.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase)
        || value.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
        || value.Contains("login failed", StringComparison.OrdinalIgnoreCase)
        || value.Contains("invalid auth", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsConfigurationFailure(string value) =>
        value.Contains("options error", StringComparison.OrdinalIgnoreCase)
        || value.Contains("invalid configuration", StringComparison.OrdinalIgnoreCase)
        || value.Contains("cannot load", StringComparison.OrdinalIgnoreCase);

    private async ValueTask<bool> TryCleanupAsync(WorkspaceProcessLaunch cleanup)
    {
        try
        {
            using var deadline = new CancellationTokenSource(CleanupTimeout);
            var result = await _commandRunner.RunAsync(
                    cleanup,
                    ReadOnlyMemory<byte>.Empty,
                    deadline.Token)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private NetworkConnectionResult<INetworkConnectionSession> CleanupFailed() => Fail(
        NetworkConnectionErrorCode.RouteUnavailable,
        $"{StablePrefix}_route_cleanup_failed",
        $"The partially started {DisplayName} route could not be removed safely from the workspace environment.",
        retryable: true);

    private NetworkConnectionResult<INetworkConnectionSession> StaleCleanupFailed() => Fail(
        NetworkConnectionErrorCode.RouteUnavailable,
        $"{StablePrefix}_stale_route_cleanup_failed",
        $"A previous {DisplayName} route could not be removed from the workspace environment.",
        retryable: true);

    private static string TokenFor(NetworkConnectionId connectionId)
    {
        var source = Encoding.UTF8.GetBytes(connectionId.Value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant()[..16];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private static string DirectoryFor(NetworkConnectionId connectionId) =>
        $"/tmp/ghostshell-network-{TokenFor(connectionId)}";

    private static void Clear(IEnumerable<byte[]> values)
    {
        foreach (var value in values)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static NetworkConnectionResult<INetworkConnectionSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<INetworkConnectionSession>.Fail(
            Error(code, stableCode, message, retryable));

    private static NetworkConnectionError Error(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        new(code, stableCode, message, retryable);

    private sealed class ResolvedVpnPlan(
        IsolatedVpnConnectionPlan plan,
        IReadOnlyList<byte[]> secretValues) : IDisposable
    {
        private int _disposed;

        public IsolatedVpnConnectionPlan Plan { get; } = plan;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Clear(secretValues);
            }
        }
    }

    private sealed class IsolatedVpnConnectionSession : INetworkConnectionSession
    {
        private readonly string _displayName;
        private readonly WorkspaceProcessLaunch _health;
        private readonly WorkspaceProcessLaunch _cleanup;
        private readonly IWorkspaceIsolationCommandRunner _commandRunner;
        private readonly TimeSpan _healthPollInterval;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly Task _healthMonitor;
        private NetworkConnectionSnapshot _snapshot;
        private int _disposed;

        public IsolatedVpnConnectionSession(
            NetworkConnectionId connectionId,
            string displayName,
            WorkspaceProcessLaunch health,
            WorkspaceProcessLaunch cleanup,
            IWorkspaceIsolationCommandRunner commandRunner,
            TimeSpan healthPollInterval)
        {
            _displayName = displayName;
            _health = health;
            _cleanup = cleanup;
            _commandRunner = commandRunner;
            _healthPollInterval = healthPollInterval;
            _snapshot = new NetworkConnectionSnapshot(
                connectionId,
                NetworkConnectionState.Connected,
                $"{displayName} is connected in the workspace environment.");
            _healthMonitor = MonitorHealthAsync();
        }

        public NetworkConnectionSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot;
                }
            }
        }

        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Attached;

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _healthMonitor.ConfigureAwait(false);
            Publish(new NetworkConnectionSnapshot(
                Snapshot.ConnectionId,
                NetworkConnectionState.Disconnecting,
                $"Disconnecting {_displayName}…"));
            try
            {
                using var deadline = new CancellationTokenSource(CleanupTimeout);
                var result = await _commandRunner.RunAsync(
                        _cleanup,
                        ReadOnlyMemory<byte>.Empty,
                        deadline.Token)
                    .ConfigureAwait(false);
                Publish(result.ExitCode == 0
                    ? new NetworkConnectionSnapshot(
                        Snapshot.ConnectionId,
                        NetworkConnectionState.Disconnected)
                    : new NetworkConnectionSnapshot(
                        Snapshot.ConnectionId,
                        NetworkConnectionState.Failed,
                        $"{_displayName} cleanup failed in the workspace environment."));
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    $"{_displayName} cleanup failed in the workspace environment."));
            }

            _lifetime.Dispose();
        }

        private async Task MonitorHealthAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_healthPollInterval, _lifetime.Token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token);
                    deadline.CancelAfter(HealthTimeout);
                    var result = await _commandRunner.RunAsync(
                            _health,
                            ReadOnlyMemory<byte>.Empty,
                            deadline.Token)
                        .ConfigureAwait(false);
                    if (result.ExitCode == 0)
                    {
                        continue;
                    }

                    PublishHealthFailure(result.ExitCode switch
                    {
                        64 => $"{_displayName} no longer provides a full workspace route.",
                        65 => $"{_displayName} can no longer carry workspace traffic.",
                        _ => $"{_displayName} stopped in the workspace environment.",
                    });
                    return;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                PublishHealthFailure(
                    $"GhostSHELL could not verify {_displayName} in the workspace environment.");
            }
        }

        private void PublishHealthFailure(string status)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Publish(new NetworkConnectionSnapshot(
                Snapshot.ConnectionId,
                NetworkConnectionState.Failed,
                status));
        }

        private void Publish(NetworkConnectionSnapshot snapshot)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
            }

            Changed?.Invoke(this, snapshot);
        }
    }
}
