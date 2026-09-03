using System.Diagnostics;
using System.Runtime.InteropServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Terminal;

namespace GhostShell.Infrastructure.Tests;

public sealed class AppleContainerWorkspaceIsolationNativeIntegrationTests
{
    private const string EnableVariable = "GHOSTSHELL_RUN_APPLE_CONTAINER_NATIVE";
    private const string TerminalRuntimePathVariable = "GHOSTSHELL_GHOSTTY_VT_PATH";

    [NativeAppleContainerFact]
    public async Task Native_provider_reconfigures_mounts_without_losing_the_persistent_root()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var workspaceId = new WorkspaceId($"native-smoke-{Guid.NewGuid():N}");
        var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(workspaceId);
        var snapshotImage = $"{resourceName}-state:latest";
        var hostMount = Directory.CreateTempSubdirectory("ghostshell-native-mount-");
        var replacementHostMount = Directory.CreateTempSubdirectory(
            "ghostshell-native-replacement-mount-");
        await File.WriteAllTextAsync(
            Path.Combine(hostMount.FullName, "host-marker"),
            "mounted",
            timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(replacementHostMount.FullName, "replacement-marker"),
            "replacement",
            timeout.Token);
        var provider = new AppleContainerWorkspaceIsolationProvider();
        var request = new WorkspaceIsolationPrepareRequest(workspaceId);
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();
        const string marker = "ghostshell-native-persistence-ok";

        try
        {
            var first = Success(await provider.PrepareAsync(request, progress, timeout.Token));
            Assert.Equal(resourceName, first.ResourceName);
            Assert.Equal(
                AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
                first.RuntimeImageReference);
            Assert.Contains(
                progress.Values,
                item => item.Status == "Downloading the workspace image…");
            Assert.Contains(
                progress.Values,
                item => item.Status == "Creating the persistent workspace isolate…");
            await VerifyIdlePromptAsync(provider, first, timeout.Token);
            var commandOutput = await RunCommandAsync(
                provider,
                first,
                "/bin/sh",
                ["-c", "printf '%s' workspace-command-ok"],
                timeout.Token);
            Assert.Equal("workspace-command-ok", commandOutput);
            var interactiveOutput = await RunInteractiveCommandAsync(
                provider,
                first,
                timeout.Token);
            Assert.Equal("relay:workspace-browser", interactiveOutput);
            await RunShellAsync(
                provider,
                first,
                "test \"$(. /etc/os-release && printf '%s' \"$ID\")\" = ubuntu\n"
                + $"printf '%s' '{marker}' > /root/.ghostshell-native-smoke\n"
                + "printf '#!/bin/sh\\nexit 0\\n' > /usr/local/bin/ghostshell-native-smoke\n"
                + "chmod +x /usr/local/bin/ghostshell-native-smoke\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(first, timeout.Token));

            var second = Success(await provider.PrepareAsync(request, timeout.Token));
            Assert.NotEqual(first.LeaseId, second.LeaseId);
            await RunShellAsync(
                provider,
                second,
                $"test \"$(cat /root/.ghostshell-native-smoke)\" = '{marker}'\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(second, timeout.Token));

            var reconfigureProgress = new RecordingProgress<WorkspaceIsolationProgress>();
            var third = Success(await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(
                    workspaceId,
                    [new WorkspaceIsolationMount(hostMount.FullName, "/workspace", true)]),
                reconfigureProgress,
                timeout.Token));
            Assert.Contains(
                reconfigureProgress.Values,
                item => item.Status == "Saving installed packages and guest files…");
            Assert.Contains(
                reconfigureProgress.Values,
                item => item.Status == "Building the preserved workspace image…");
            await RunShellAsync(
                provider,
                third,
                $"test \"$(cat /root/.ghostshell-native-smoke)\" = '{marker}'\n"
                + "ghostshell-native-smoke\n"
                + "test \"$(cat /workspace/host-marker)\" = mounted\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(third, timeout.Token));

            var fourth = Success(await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(
                    workspaceId,
                    [
                        new WorkspaceIsolationMount(
                            replacementHostMount.FullName,
                            "/workspace",
                            true),
                    ]),
                timeout.Token));
            await RunShellAsync(
                provider,
                fourth,
                "ghostshell-native-smoke\n"
                + "test ! -e /workspace/host-marker\n"
                + "test \"$(cat /workspace/replacement-marker)\" = replacement\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(fourth, timeout.Token));

            var recreateProgress = new RecordingProgress<WorkspaceIsolationProgress>();
            _ = Success(await provider.RecreateAsync(
                request,
                recreateProgress,
                timeout.Token));
            Assert.Contains(
                recreateProgress.Values,
                item => item.Status == "Removing the existing workspace environment…");
            var recreated = Success(await provider.PrepareAsync(request, timeout.Token));
            await RunShellAsync(
                provider,
                recreated,
                "test ! -e /root/.ghostshell-native-smoke\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(recreated, timeout.Token));
        }
        finally
        {
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["delete", "--force", resourceName],
                standardInput: null,
                CancellationToken.None);
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["image", "delete", snapshotImage],
                standardInput: null,
                CancellationToken.None);
            hostMount.Delete(recursive: true);
            replacementHostMount.Delete(recursive: true);
        }
    }

    private static async Task VerifyIdlePromptAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var process = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                mode: WorkspaceProcessMode.Interactive
                    | WorkspaceProcessMode.AllocateTerminal)));
        var launch = new TerminalLaunchRequest(
            process.HostWorkingDirectory,
            process.Executable,
            process.Arguments,
            process.Environment,
            shellActivityFallback: TerminalShellActivityFallback.PromptShape);
        var configuredRuntime = Environment.GetEnvironmentVariable(
            TerminalRuntimePathVariable);
        if (string.IsNullOrWhiteSpace(configuredRuntime))
        {
            Environment.SetEnvironmentVariable(
                TerminalRuntimePathVariable,
                StagedTerminalRuntimePath());
        }

        try
        {
            var factory = new GhosttyVtTerminalSessionFactory();
            await using var session = await factory.CreateAsync(
                SessionId.New(),
                launch,
                cancellationToken);
            try
            {
                using var promptTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                promptTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                TerminalScreenSnapshot screen;
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), promptTimeout.Token);
                    screen = await session.ReadScreenAsync(promptTimeout.Token);
                }
                while (!screen.PlainText.Contains('#', StringComparison.Ordinal));

                var snapshot = await session.SnapshotAsync(promptTimeout.Token);
                Assert.False(
                    snapshot.HasActiveWork,
                    $"The real Apple container shell prompt was classified as active: {screen.PlainText}");
            }
            finally
            {
                _ = await session.CloseAsync(PanelCloseMode.Force, CancellationToken.None);
            }
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(configuredRuntime))
            {
                Environment.SetEnvironmentVariable(TerminalRuntimePathVariable, null);
            }
        }
    }

    private static string StagedTerminalRuntimePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                continue;
            }

            var runtime = Path.Combine(
                directory.FullName,
                "native",
                "artifacts",
                "osx-arm64",
                "libghostty-vt.dylib");
            Assert.True(File.Exists(runtime), $"The staged terminal runtime is missing: {runtime}");
            return runtime;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the GhostSHELL repository above {AppContext.BaseDirectory}.");
    }

    private static async Task RunShellAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        string input,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                mode: WorkspaceProcessMode.Interactive)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            input,
            cancellationToken);

        Assert.True(
            result.ExitCode == 0,
            $"Apple container shell exited with {result.ExitCode}: {result.StandardError}");
    }

    private static async Task<string> RunCommandAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                executable,
                arguments)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            standardInput: null,
            cancellationToken);
        Assert.True(
            result.ExitCode == 0,
            $"Apple container command exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task<string> RunInteractiveCommandAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", "read value; printf 'relay:%s' \"$value\""],
                mode: WorkspaceProcessMode.Interactive)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            "workspace-browser\n",
            cancellationToken);
        Assert.True(
            result.ExitCode == 0,
            $"Apple container relay exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), $"Failed to start '{executable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static T Success<T>(WorkspaceIsolationResult<T> result) =>
        Assert.IsType<WorkspaceIsolationResult<T>.Success>(result).Value;

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class NativeAppleContainerFactAttribute : FactAttribute
    {
        public NativeAppleContainerFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnableVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {EnableVariable}=1 to exercise the installed Apple container runtime.";
                return;
            }

            if (!OperatingSystem.IsMacOS()
                || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                Skip = "The Apple container native test requires Apple-silicon macOS.";
            }
        }
    }
}
