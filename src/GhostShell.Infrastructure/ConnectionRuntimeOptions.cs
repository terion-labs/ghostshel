namespace GhostShell.Infrastructure;

public sealed record ConnectionRuntimeOptions(
    ConnectionHostPlatform Platform,
    string DefaultShell)
{
    public static ConnectionRuntimeOptions Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Windows,
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.MacOs,
                Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh");
        }

        if (OperatingSystem.IsLinux())
        {
            return new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh");
        }

        return new ConnectionRuntimeOptions(ConnectionHostPlatform.Other, string.Empty);
    }
}
