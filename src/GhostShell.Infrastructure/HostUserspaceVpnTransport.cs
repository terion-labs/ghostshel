using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface IHostUserspaceVpnTransport
{
    ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts only userspace VPN engines that expose a loopback SOCKS5 listener. It never
/// asks an engine to create a TUN device and never changes host routes.
/// </summary>
internal sealed class HostUserspaceVpnTransport : IHostUserspaceVpnTransport
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReachabilityRetryInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultHealthPollInterval = TimeSpan.FromSeconds(30);
    private const int LoopbackBindAttempts = 3;
    private readonly NetworkConnectionKind _kind;
    private readonly ISecretVault _secretVault;
    private readonly IConnectionExecutableLocator _executableLocator;
    private readonly IHostVpnProcessRunner _processRunner;
    private readonly ISocksReachabilityProbe _reachabilityProbe;
    private readonly TimeSpan _healthPollInterval;
    private readonly string _persistentStateRoot;

    public HostUserspaceVpnTransport(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator)
        : this(
            kind,
            secretVault,
            executableLocator,
            new HostUserspaceVpnProcessRunner(),
            Path.Combine(GhostShellDataPaths.CreateDefault().DataDirectory, "vpn-state"),
            new SocksReachabilityProbe())
    {
    }

    internal HostUserspaceVpnTransport(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IHostVpnProcessRunner processRunner,
        string persistentStateRoot,
        ISocksReachabilityProbe? reachabilityProbe = null,
        TimeSpan? healthPollInterval = null)
    {
        if (kind is not (
                NetworkConnectionKind.WireGuard
                or NetworkConnectionKind.OpenVpn
                or NetworkConnectionKind.AnyConnect
                or NetworkConnectionKind.Tailscale))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        _kind = kind;
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _executableLocator = executableLocator
            ?? throw new ArgumentNullException(nameof(executableLocator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _reachabilityProbe = reachabilityProbe ?? new SocksReachabilityProbe();
        _healthPollInterval = healthPollInterval ?? DefaultHealthPollInterval;
        if (_healthPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(healthPollInterval),
                healthPollInterval,
                "The host VPN health polling interval must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(persistentStateRoot);
        _persistentStateRoot = Path.GetFullPath(persistentStateRoot);
    }

    public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Placement is not WorkspaceNetworkPlacement.HostPlacement)
        {
            throw new ArgumentException(
                "A host userspace VPN transport requires host placement.",
                nameof(request));
        }

        return _kind switch
        {
            NetworkConnectionKind.WireGuard => ConnectWireGuardAsync(
                request.Connection,
                progress,
                cancellationToken),
            NetworkConnectionKind.OpenVpn => ValueTask.FromResult(Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                "openvpn_host_userspace_adapter_missing",
                "App-scoped OpenVPN requires OpenVPN 3 Core connected to a userspace IP stack. This build does not yet include that native adapter; installing the OpenVPN CLI alone is not sufficient.",
                retryable: false)),
            NetworkConnectionKind.AnyConnect => ConnectAnyConnectAsync(
                request,
                progress,
                cancellationToken),
            NetworkConnectionKind.Tailscale => ConnectTailscaleAsync(
                request,
                progress,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(_kind), _kind, null),
        };
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectWireGuardAsync(
            NetworkConnectionProfile profile,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (profile.Configuration is not NetworkConnectionConfiguration.WireGuard configuration)
        {
            return InvalidConfiguration("WireGuard");
        }

        var executable = _executableLocator.Find("wireproxy");
        if (executable is null)
        {
            return RuntimeMissing(
                "wireguard_host_userspace_runtime_missing",
                "Install the userspace wireproxy executable and make it available on PATH. GhostSHELL will run it only as a loopback SOCKS5 proxy; it will not create a host interface.");
        }

        var resolved = await ResolveSecretAsync(
                profile.Id,
                configuration.ConfigurationSecret,
                "WireGuard configuration",
                isCredential: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
        }

        var secret = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        await using var temporary = SecureHostVpnDirectory.Create();
        try
        {
            for (var attempt = 0; attempt < LoopbackBindAttempts; attempt++)
            {
                var port = AllocateLoopbackPort();
                var suffix = Encoding.UTF8.GetBytes(
                    $"\n\n[Socks5]\nBindAddress = 127.0.0.1:{port}\n");
                try
                {
                    var configurationPath = await temporary.WriteAsync(
                            $"wireproxy-{attempt}.conf",
                            [secret, suffix],
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new NetworkConnectionProgress(
                        "Validating the WireGuard userspace configuration…"));
                    var validation = await RunCommandAsync(
                            new HostVpnProcessRequest(
                                executable,
                                ["-c", configurationPath, "-n"],
                                ReadOnlyMemory<byte>.Empty),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (validation is null)
                    {
                        return CommandTimedOut("WireGuard");
                    }

                    if (validation.ExitCode != 0)
                    {
                        return Fail(
                            NetworkConnectionErrorCode.InvalidConfiguration,
                            "wireguard_host_configuration_invalid",
                            "wireproxy rejected the WireGuard configuration.",
                            retryable: false);
                    }

                    progress?.Report(new NetworkConnectionProgress(
                        "Connecting WireGuard through the app-scoped userspace network…"));
                    var process = await StartProcessAsync(
                            new HostVpnProcessRequest(
                                executable,
                                ["-c", configurationPath, "-s"],
                                ReadOnlyMemory<byte>.Empty),
                            "WireGuard",
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (process is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
                    {
                        return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                            startFailure.Error);
                    }

                    var running = ((NetworkConnectionResult<IHostVpnProcess>.Success)process).Value;
                    var listener = await WaitForListenerOrDisposeAsync(
                            running,
                            port,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (listener.Ready)
                    {
                        var reachability = await VerifyReachabilityAsync(
                                "WireGuard",
                                port,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!reachability.IsReachable)
                        {
                            await running.DisposeAsync().ConfigureAwait(false);
                            return ReachabilityFailed("WireGuard", reachability.Failure);
                        }

                        var directory = temporary.TransferOwnership();
                        return Succeed(profile.Id, "WireGuard", port, [running], directory);
                    }

                    if (!listener.ProcessExited || attempt == LoopbackBindAttempts - 1)
                    {
                        return ConnectionFailed("WireGuard", listener.Diagnostic);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(suffix);
                }
            }

            throw new InvalidOperationException("The WireGuard startup retry loop did not return.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectAnyConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        var profile = request.Connection;
        if (profile.Configuration is not NetworkConnectionConfiguration.AnyConnect configuration)
        {
            return InvalidConfiguration("Cisco AnyConnect");
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeMissing(
                "anyconnect_host_userspace_windows_unavailable",
                "App-scoped Cisco AnyConnect currently requires OpenConnect script-tun and ocproxy, whose userspace socket transport is unavailable on Windows.");
        }

        var openconnect = _executableLocator.Find("openconnect");
        var ocproxy = _executableLocator.Find("ocproxy");
        if (openconnect is null || ocproxy is null)
        {
            return RuntimeMissing(
                "anyconnect_host_userspace_runtime_missing",
                "Install both openconnect and ocproxy and make them available on PATH. GhostSHELL uses OpenConnect's script-tun mode, so no host TUN interface or route is created.");
        }

        byte[]? password = null;
        byte[]? certificate = null;
        byte[]? standardInput = null;
        await using var temporary = SecureHostVpnDirectory.Create();
        try
        {
            if (configuration.PasswordSecret is { } passwordReference)
            {
                var resolved = await ResolveSecretAsync(
                        profile.Id,
                        passwordReference,
                        "Cisco AnyConnect password",
                        isCredential: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
                }

                password = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
            }
            else if (request.TransientPassword is { } transientPassword)
            {
                password = GC.AllocateUninitializedArray<byte>(transientPassword.Length);
                transientPassword.CopyTo(password);
            }

            if (password is not null)
            {
                standardInput = new byte[password.Length + 1];
                password.CopyTo(standardInput, 0);
                standardInput[^1] = (byte)'\n';
            }

            string? certificatePath = null;
            if (configuration.ClientCertificateSecret is { } certificateReference)
            {
                var resolved = await ResolveSecretAsync(
                        profile.Id,
                        certificateReference,
                        "Cisco AnyConnect client certificate",
                        isCredential: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
                }

                certificate = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
                certificatePath = await temporary.WriteAsync(
                        "client-certificate",
                        [certificate],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            for (var attempt = 0; attempt < LoopbackBindAttempts; attempt++)
            {
                var port = AllocateLoopbackPort();
                var arguments = new List<string>
                {
                    "--script-tun",
                    "--script",
                    $"{ShellQuote(ocproxy)} -D {port}",
                    "--non-inter",
                };
                if (configuration.Username is not null)
                {
                    arguments.Add("--user");
                    arguments.Add(configuration.Username);
                }

                if (configuration.AuthenticationGroup is not null)
                {
                    arguments.Add("--authgroup");
                    arguments.Add(configuration.AuthenticationGroup);
                }

                if (certificatePath is not null)
                {
                    arguments.Add("--certificate");
                    arguments.Add(certificatePath);
                }

                if (standardInput is not null)
                {
                    arguments.Add("--passwd-on-stdin");
                }

                arguments.Add(configuration.Gateway.AbsoluteUri);
                progress?.Report(new NetworkConnectionProgress(
                    "Connecting Cisco AnyConnect through an app-scoped SOCKS5 proxy…"));
                var process = await StartProcessAsync(
                        new HostVpnProcessRequest(
                            openconnect,
                            arguments,
                            standardInput ?? ReadOnlyMemory<byte>.Empty),
                        "Cisco AnyConnect",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (process is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                        startFailure.Error);
                }

                var running = ((NetworkConnectionResult<IHostVpnProcess>.Success)process).Value;
                var listener = await WaitForListenerOrDisposeAsync(
                        running,
                        port,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (listener.Ready)
                {
                    var reachability = await VerifyReachabilityAsync(
                            "Cisco AnyConnect",
                            port,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!reachability.IsReachable)
                    {
                        var stopped = running.HasExited;
                        var diagnostic = running.Diagnostic;
                        await running.DisposeAsync().ConfigureAwait(false);
                        return stopped
                            ? ProcessStopped("Cisco AnyConnect", diagnostic)
                            : ReachabilityFailed("Cisco AnyConnect", reachability.Failure);
                    }

                    var directory = temporary.TransferOwnership();
                    return Succeed(
                        profile.Id,
                        "Cisco AnyConnect",
                        port,
                        [running],
                        directory);
                }

                if (!listener.ProcessExited || attempt == LoopbackBindAttempts - 1)
                {
                    return ConnectionFailed("Cisco AnyConnect", listener.Diagnostic);
                }
            }

            throw new InvalidOperationException(
                "The Cisco AnyConnect startup retry loop did not return.");
        }
        finally
        {
            Clear(password);
            Clear(certificate);
            Clear(standardInput);
        }
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectTailscaleAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (request.Connection.Configuration is not NetworkConnectionConfiguration.Tailscale
            configuration)
        {
            return InvalidConfiguration("Tailscale");
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeMissing(
                "tailscale_host_userspace_windows_unavailable",
                "A private app-scoped tailscaled control socket is not yet available on Windows in this build. GhostSHELL will not reuse the system-wide Tailscale service.");
        }

        var tailscaled = _executableLocator.Find("tailscaled");
        var tailscale = _executableLocator.Find("tailscale");
        if (tailscaled is null || tailscale is null)
        {
            return RuntimeMissing(
                "tailscale_host_userspace_runtime_missing",
                "Install both tailscale and tailscaled and make them available on PATH. GhostSHELL starts a private userspace-networking daemon with a loopback SOCKS5 listener.");
        }

        var statePath = PersistentTailscaleStatePath(
            request.WorkspaceId,
            request.Connection.Id);
        var hasPersistentIdentity = File.Exists(statePath);
        if (configuration.AuthKeySecret is not { } && !hasPersistentIdentity)
        {
            return Fail(
                NetworkConnectionErrorCode.AuthenticationRequired,
                "tailscale_host_auth_key_required",
                "The first app-scoped Tailscale connection needs a stored reusable auth key because it does not reuse the host Tailscale login.",
                retryable: false);
        }

        byte[]? authKey = null;
        if (configuration.AuthKeySecret is { } authReference)
        {
            var resolved = await ResolveSecretAsync(
                    request.Connection.Id,
                    authReference,
                    "Tailscale auth key",
                    isCredential: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
            {
                return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
            }

            authKey = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        }

        await using var temporary = SecureHostVpnDirectory.Create();
        IHostVpnProcess? daemon = null;
        try
        {
            string? authPath = null;
            if (authKey is not null)
            {
                authPath = await temporary.WriteAsync(
                        "auth-key",
                        [authKey],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var socketPath = temporary.PathFor("tailscaled.sock");
            var port = AllocateLoopbackPort();
            progress?.Report(new NetworkConnectionProgress(
                "Starting a private Tailscale userspace network…"));
            var daemonResult = await StartProcessAsync(
                    new HostVpnProcessRequest(
                        tailscaled,
                        [
                            $"--state={statePath}",
                            $"--socket={socketPath}",
                            "--tun=userspace-networking",
                            $"--socks5-server=127.0.0.1:{port}",
                        ],
                        ReadOnlyMemory<byte>.Empty),
                    "Tailscale",
                    cancellationToken)
                .ConfigureAwait(false);
            if (daemonResult is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
            {
                return NetworkConnectionResult<INetworkConnectionSession>.Fail(startFailure.Error);
            }

            daemon = ((NetworkConnectionResult<IHostVpnProcess>.Success)daemonResult).Value;
            if (!await WaitForPathAsync(
                    socketPath,
                    daemon,
                    CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return ConnectionFailed("Tailscale", daemon.Diagnostic);
            }

            ProtectFile(statePath);

            var upArguments = new List<string>
            {
                $"--socket={socketPath}",
                "up",
                $"--exit-node={configuration.ExitNode}",
                "--exit-node-allow-lan-access=false",
                "--accept-routes=true",
                "--shields-up=true",
                $"--hostname=ghostshell-{TokenFor(request.WorkspaceId.Value)}",
            };
            if (authPath is not null)
            {
                upArguments.Add($"--auth-key=file:{authPath}");
            }

            if (configuration.ControlServer is not null)
            {
                upArguments.Add($"--login-server={configuration.ControlServer.AbsoluteUri}");
            }

            progress?.Report(new NetworkConnectionProgress(
                "Authenticating the private Tailscale network…"));
            var up = await RunCommandAsync(
                    new HostVpnProcessRequest(
                        tailscale,
                        upArguments,
                        ReadOnlyMemory<byte>.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
            if (up is null)
            {
                return CommandTimedOut("Tailscale");
            }

            if (up.ExitCode != 0)
            {
                return IsAuthenticationFailure(up.Diagnostic)
                    ? AuthenticationFailed("Tailscale")
                    : ConnectionFailed("Tailscale", up.Diagnostic);
            }

            var status = await RunCommandAsync(
                    new HostVpnProcessRequest(
                        tailscale,
                        [$"--socket={socketPath}", "status", "--json"],
                        ReadOnlyMemory<byte>.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
            if (status is null
                || status.ExitCode != 0
                || !HasRunningTailscaleBackend(status.Diagnostic))
            {
                return ConnectionFailed("Tailscale", status?.Diagnostic ?? string.Empty);
            }

            if (!await _processRunner.WaitForTcpListenerAsync(
                    daemon,
                    port,
                    CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return ConnectionFailed("Tailscale", daemon.Diagnostic);
            }

            var reachability = await VerifyReachabilityAsync(
                    "Tailscale",
                    port,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reachability.IsReachable)
            {
                return ReachabilityFailed("Tailscale", reachability.Failure);
            }

            var directory = temporary.TransferOwnership();
            var connected = Succeed(
                request.Connection.Id,
                "Tailscale",
                port,
                [daemon],
                directory);
            daemon = null;
            return connected;
        }
        finally
        {
            Clear(authKey);
            if (daemon is not null)
            {
                await daemon.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolveSecretAsync(
        NetworkConnectionId connectionId,
        SecretRef reference,
        string label,
        bool isCredential,
        CancellationToken cancellationToken)
    {
        SecretVaultResult<SecretMaterial> result;
        try
        {
            result = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        reference,
                        new SecretScope(
                            SecretScopeKind.NetworkConnection,
                            connectionId.Value),
                        new SecretUsePurpose(
                            SecretUseKind.NetworkConnectionAuthentication,
                            connectionId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "host_userspace_vpn_secret_cancelled",
                $"Access to the {label} was cancelled.",
                retryable: false));
        }

        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            var authentication = failure.Error.Code is
                SecretVaultErrorCode.AuthenticationRequired or SecretVaultErrorCode.UserCancelled;
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                authentication || isCredential
                    ? NetworkConnectionErrorCode.AuthenticationRequired
                    : NetworkConnectionErrorCode.InvalidConfiguration,
                authentication || isCredential
                    ? "host_userspace_vpn_secret_access_required"
                    : "host_userspace_vpn_configuration_missing",
                authentication || isCredential
                    ? $"Authentication is required to access the {label}."
                    : $"The {label} is unavailable or invalid.",
                failure.Error.Retryable || authentication));
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = GC.AllocateUninitializedArray<byte>(material.Length);
        material.CopyTo(bytes);
        return NetworkConnectionResult<byte[]>.Succeed(bytes);
    }

    private async ValueTask<NetworkConnectionResult<IHostVpnProcess>> StartProcessAsync(
        HostVpnProcessRequest request,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var process = await _processRunner.StartAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return NetworkConnectionResult<IHostVpnProcess>.Succeed(process);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<IHostVpnProcess>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "host_userspace_vpn_cancelled",
                $"Connecting {displayName} was cancelled.",
                retryable: false));
        }
        catch (IOException)
        {
            return NetworkConnectionResult<IHostVpnProcess>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.ConnectionFailed,
                "host_userspace_vpn_process_start_failed",
                $"The {displayName} userspace process could not be started.",
                retryable: true));
        }
    }

    private async ValueTask<HostVpnCommandResult?> RunCommandAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CommandTimeout);
        try
        {
            return await _processRunner.RunAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException exception)
        {
            return new HostVpnCommandResult(-1, exception.Message);
        }
    }

    private async ValueTask<SocksReachabilityResult> VerifyReachabilityAsync(
        string displayName,
        int socksPort,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new NetworkConnectionProgress(
            $"Verifying {displayName} app-scoped reachability…"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReachabilityTimeout);
        var lastResult = new SocksReachabilityResult(
            SocksReachabilityFailure.TransportFailed);
        try
        {
            while (true)
            {
                lastResult = await _reachabilityProbe.ProbeAsync(socksPort, deadline.Token)
                    .ConfigureAwait(false);
                if (lastResult.IsReachable || IsDefinitiveProbeFailure(lastResult.Failure))
                {
                    return lastResult;
                }

                await Task.Delay(ReachabilityRetryInterval, deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return lastResult.Failure == SocksReachabilityFailure.TransportFailed
                ? new(SocksReachabilityFailure.TimedOut)
                : lastResult;
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return new(SocksReachabilityFailure.TransportFailed);
        }
    }

    private static bool IsDefinitiveProbeFailure(SocksReachabilityFailure failure) => failure is
        SocksReachabilityFailure.SocksHandshakeRejected
        or SocksReachabilityFailure.DestinationRejected
        or SocksReachabilityFailure.TlsRejected
        or SocksReachabilityFailure.InvalidHttpResponse;

    private async ValueTask<HostVpnListenerResult> WaitForListenerOrDisposeAsync(
        IHostVpnProcess process,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            var ready = await _processRunner.WaitForTcpListenerAsync(
                    process,
                    port,
                    ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (ready)
            {
                return new HostVpnListenerResult(true, false, string.Empty);
            }

            var result = new HostVpnListenerResult(
                false,
                process.HasExited,
                process.Diagnostic);
            await process.DisposeAsync().ConfigureAwait(false);
            return result;
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<bool> WaitForPathAsync(
        string path,
        IHostVpnProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!process.HasExited)
            {
                if (File.Exists(path))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private NetworkConnectionResult<INetworkConnectionSession> ConnectionFailed(
        string displayName,
        string diagnostic) => IsAuthenticationFailure(diagnostic)
            ? AuthenticationFailed(displayName)
            : Fail(
                NetworkConnectionErrorCode.ConnectionFailed,
                $"{_kind.ToString().ToLowerInvariant()}_host_userspace_connection_failed",
                $"{displayName} did not expose a ready app-scoped SOCKS5 route.",
                retryable: true);

    private NetworkConnectionResult<INetworkConnectionSession> AuthenticationFailed(
        string displayName) => Fail(
        NetworkConnectionErrorCode.AuthenticationRequired,
        $"{_kind.ToString().ToLowerInvariant()}_host_authentication_failed",
        $"{displayName} rejected the supplied authentication.",
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> InvalidConfiguration(
        string displayName) => Fail(
        NetworkConnectionErrorCode.InvalidConfiguration,
        $"{_kind.ToString().ToLowerInvariant()}_host_configuration_invalid",
        $"The selected connection does not contain a {displayName} configuration.",
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> CommandTimedOut(
        string displayName) => Fail(
        NetworkConnectionErrorCode.ConnectionFailed,
        $"{_kind.ToString().ToLowerInvariant()}_host_command_timed_out",
        $"{displayName} did not finish the current connection step in time.",
        retryable: true);

    private NetworkConnectionResult<INetworkConnectionSession> ReachabilityFailed(
        string displayName,
        SocksReachabilityFailure failure) => Fail(
        NetworkConnectionErrorCode.RouteUnavailable,
        $"{_kind.ToString().ToLowerInvariant()}_host_reachability_failed",
        DescribeReachabilityFailure(displayName, failure),
        retryable: true);

    private NetworkConnectionResult<INetworkConnectionSession> ProcessStopped(
        string displayName,
        string diagnostic) => IsAuthenticationFailure(diagnostic)
            ? AuthenticationFailed(displayName)
            : Fail(
                NetworkConnectionErrorCode.ConnectionFailed,
                $"{_kind.ToString().ToLowerInvariant()}_host_process_stopped",
                $"{displayName} stopped while GhostSHELL was testing its app-scoped route. Check the gateway, login group, and server policy.",
                retryable: true);

    private static string DescribeReachabilityFailure(
        string displayName,
        SocksReachabilityFailure failure) =>
        failure switch
        {
            SocksReachabilityFailure.ListenerUnavailable =>
                $"{displayName}'s local SOCKS5 route stopped before GhostSHELL could test it.",
            SocksReachabilityFailure.SocksHandshakeRejected =>
                $"{displayName}'s local SOCKS5 route rejected GhostSHELL's health check.",
            SocksReachabilityFailure.DestinationRejected =>
                $"{displayName} connected, but the VPN rejected connections to the public health-check peers. The VPN may allow only internal routes or its gateway may block Internet access.",
            SocksReachabilityFailure.TlsRejected =>
                $"{displayName} reached a public health-check peer, but TLS validation failed. Check whether the VPN intercepts TLS traffic.",
            SocksReachabilityFailure.InvalidHttpResponse =>
                $"{displayName} reached a public health-check peer, but it returned an invalid response.",
            SocksReachabilityFailure.TimedOut =>
                $"{displayName} timed out while reaching the public health-check peers. The VPN may allow only internal routes or its gateway may block Internet access.",
            _ =>
                $"{displayName} connected, but traffic failed while crossing its app-scoped route.",
        };

    private static NetworkConnectionResult<INetworkConnectionSession> RuntimeMissing(
        string stableCode,
        string message) => Fail(
        NetworkConnectionErrorCode.RuntimeMissing,
        stableCode,
        message,
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> Succeed(
        NetworkConnectionId connectionId,
        string displayName,
        int port,
        IReadOnlyList<IHostVpnProcess> processes,
        string temporaryDirectory,
        HostVpnProcessRequest? cleanup = null) =>
        NetworkConnectionResult<INetworkConnectionSession>.Succeed(
            new HostUserspaceVpnSession(
                connectionId,
                displayName,
                port,
                processes,
                temporaryDirectory,
                cleanup,
                _processRunner,
                _reachabilityProbe,
                _healthPollInterval));

    private static NetworkConnectionResult<INetworkConnectionSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<INetworkConnectionSession>.Fail(
            new NetworkConnectionError(code, stableCode, message, retryable));

    private static bool IsAuthenticationFailure(string diagnostic) =>
        diagnostic.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("login failed", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("needslogin", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("invalid auth", StringComparison.OrdinalIgnoreCase);

    private static bool HasRunningTailscaleBackend(string diagnostic) =>
        diagnostic.Contains("\"BackendState\"", StringComparison.Ordinal)
        && diagnostic.Contains("\"Running\"", StringComparison.Ordinal);

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string TokenFor(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string PersistentTailscaleStatePath(
        WorkspaceInstanceId workspaceId,
        NetworkConnectionId connectionId)
    {
        var directory = Directory.CreateDirectory(Path.Combine(
            _persistentStateRoot,
            TokenFor(workspaceId.Value)));
        if (!OperatingSystem.IsWindows())
        {
            directory.UnixFileMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute;
        }

        return Path.Combine(directory.FullName, $"{TokenFor(connectionId.Value)}.state");
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed record HostVpnListenerResult(
        bool Ready,
        bool ProcessExited,
        string Diagnostic);

    private sealed class HostUserspaceVpnSession : INetworkConnectionSession
    {
        private readonly IReadOnlyList<IHostVpnProcess> _processes;
        private readonly string _displayName;
        private readonly string _temporaryDirectory;
        private readonly HostVpnProcessRequest? _cleanup;
        private readonly IHostVpnProcessRunner _processRunner;
        private readonly ISocksReachabilityProbe _reachabilityProbe;
        private readonly int _socksPort;
        private readonly TimeSpan _healthPollInterval;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly Task _processMonitor;
        private readonly Task _healthMonitor;
        private NetworkConnectionSnapshot _snapshot;
        private int _disposed;

        public HostUserspaceVpnSession(
            NetworkConnectionId connectionId,
            string displayName,
            int port,
            IReadOnlyList<IHostVpnProcess> processes,
            string temporaryDirectory,
            HostVpnProcessRequest? cleanup,
            IHostVpnProcessRunner processRunner,
            ISocksReachabilityProbe reachabilityProbe,
            TimeSpan healthPollInterval)
        {
            _processes = processes;
            _displayName = displayName;
            _temporaryDirectory = temporaryDirectory;
            _cleanup = cleanup;
            _processRunner = processRunner;
            _reachabilityProbe = reachabilityProbe;
            _socksPort = port;
            _healthPollInterval = healthPollInterval;
            _snapshot = new NetworkConnectionSnapshot(
                connectionId,
                NetworkConnectionState.Connected,
                $"{displayName} is connected through an app-scoped userspace network.");
            Egress = WorkspaceNetworkEgress.ViaProxy(
                new Uri($"socks5://127.0.0.1:{port}", UriKind.Absolute));
            _processMonitor = MonitorProcessesAsync();
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

        public WorkspaceNetworkEgress Egress { get; }

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_processMonitor, _healthMonitor).ConfigureAwait(false);

            if (_cleanup is not null)
            {
                using var deadline = new CancellationTokenSource(CommandTimeout);
                try
                {
                    _ = await _processRunner.RunAsync(_cleanup, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                }
            }

            for (var index = _processes.Count - 1; index >= 0; index--)
            {
                await _processes[index].DisposeAsync().ConfigureAwait(false);
            }

            SecureHostVpnDirectory.DeleteOwned(_temporaryDirectory);
            Publish(new NetworkConnectionSnapshot(
                Snapshot.ConnectionId,
                NetworkConnectionState.Disconnected));
            _lifetime.Dispose();
        }

        private async Task MonitorProcessesAsync()
        {
            try
            {
                var monitors = _processes
                    .Select(process => process.WaitForExitAsync(_lifetime.Token))
                    .ToArray();
                await Task.WhenAny(monitors).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    "The app-scoped VPN process stopped unexpectedly."));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    "The app-scoped VPN process could no longer be monitored."));
            }
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
                    deadline.CancelAfter(ReachabilityTimeout);
                    var reachability = await _reachabilityProbe.ProbeAsync(
                            _socksPort,
                            deadline.Token)
                        .ConfigureAwait(false);
                    if (reachability.IsReachable)
                    {
                        continue;
                    }

                    Publish(new NetworkConnectionSnapshot(
                        Snapshot.ConnectionId,
                        NetworkConnectionState.Failed,
                        DescribeReachabilityFailure(_displayName, reachability.Failure)));
                    return;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is
                IOException or OperationCanceledException or SocketException)
            {
                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    "The app-scoped VPN reachability check failed."));
            }
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

    private sealed class SecureHostVpnDirectory : IAsyncDisposable
    {
        private const string Prefix = "ghostshell-vpn-";
        private bool _owned = true;

        private SecureHostVpnDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static SecureHostVpnDirectory Create()
        {
            var directory = Directory.CreateTempSubdirectory(Prefix);
            if (!OperatingSystem.IsWindows())
            {
                directory.UnixFileMode = UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute;
            }

            return new SecureHostVpnDirectory(directory.FullName);
        }

        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);

        public async ValueTask<string> WriteAsync(
            string fileName,
            IReadOnlyList<ReadOnlyMemory<byte>> content,
            CancellationToken cancellationToken)
        {
            var path = PathFor(fileName);
            await using (var stream = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (var part in content)
                {
                    await stream.WriteAsync(part, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return path;
        }

        public string TransferOwnership()
        {
            _owned = false;
            return Path;
        }

        public ValueTask DisposeAsync()
        {
            if (_owned)
            {
                DeleteOwned(Path);
            }

            return ValueTask.CompletedTask;
        }

        public static void DeleteOwned(string path)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.Ordinal)
                || !System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a directory not created by the host VPN transport.");
            }

            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
