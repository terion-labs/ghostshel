using System.Globalization;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Encodes OpenSSH policy as individual argv entries and keeps endpoint values behind an option boundary.
/// </summary>
internal static class SshConnectionArguments
{
    public static IReadOnlyList<string> Open(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding = null)
    {
        var arguments = Common(profile, endpoint, knownHostBinding);
        foreach (var variable in profile.Startup.Environment)
        {
            AddOption(arguments, "SendEnv", variable.Name);
        }

        arguments.Add("-tt");
        AddDestination(arguments, endpoint);
        if (profile.Startup.Directory is { } directory)
        {
            arguments.Add(BuildRemoteLoginCommand(directory));
        }

        return Array.AsReadOnly(arguments.ToArray());
    }

    public static IReadOnlyList<string> Probe(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding = null)
    {
        var arguments = Common(profile, endpoint, knownHostBinding);
        AddOption(arguments, "BatchMode", "yes");
        AddOption(arguments, "ConnectTimeout", "10");
        AddDestination(arguments, endpoint);
        arguments.Add("true");
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static List<string> Common(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding)
    {
        var arguments = new List<string>
        {
            "-p",
            endpoint.Port.ToString(CultureInfo.InvariantCulture),
        };
        AddOption(arguments, "StrictHostKeyChecking", profile.HostKeyPolicy switch
        {
            SshHostKeyPolicy.Strict => "yes",
            SshHostKeyPolicy.AcceptNew => "accept-new",
            SshHostKeyPolicy.InsecureIgnore => "no",
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.HostKeyPolicy,
                "SSH host-key policy is invalid."),
        });
        if (profile.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore)
        {
            AddOption(arguments, "UserKnownHostsFile", NullKnownHostsPath());
        }
        else if (knownHostBinding is not null)
        {
            AddOption(arguments, "UserKnownHostsFile", knownHostBinding.FilePath);
            AddOption(arguments, "GlobalKnownHostsFile", NullKnownHostsPath());
            AddOption(arguments, "HostKeyAlias", knownHostBinding.Alias);
        }

        if (profile.KeepAlive.Enabled)
        {
            var seconds = (int)Math.Ceiling(profile.KeepAlive.Interval.TotalSeconds);
            AddOption(arguments, "ServerAliveInterval", seconds.ToString(CultureInfo.InvariantCulture));
            AddOption(
                arguments,
                "ServerAliveCountMax",
                profile.KeepAlive.MaximumFailures.ToString(CultureInfo.InvariantCulture));
        }

        AddAuthentication(arguments, profile.Authentication);
        return arguments;
    }

    private static void AddAuthentication(
        List<string> arguments,
        ConnectionAuthentication authentication)
    {
        switch (authentication)
        {
            case ConnectionAuthentication.None:
                return;
            case ConnectionAuthentication.SshAgent:
                AddOption(arguments, "PreferredAuthentications", "publickey");
                AddOption(arguments, "PubkeyAuthentication", "yes");
                AddOption(arguments, "PasswordAuthentication", "no");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                return;
            case ConnectionAuthentication.Password:
                AddOption(arguments, "PreferredAuthentications", "password");
                AddOption(arguments, "PubkeyAuthentication", "no");
                AddOption(arguments, "PasswordAuthentication", "yes");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                AddOption(arguments, "NumberOfPasswordPrompts", "1");
                AddOption(arguments, "BatchMode", "no");
                return;
            case ConnectionAuthentication.PrivateKey:
                AddOption(arguments, "PreferredAuthentications", "publickey");
                AddOption(arguments, "PubkeyAuthentication", "yes");
                AddOption(arguments, "PasswordAuthentication", "no");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                AddOption(arguments, "IdentitiesOnly", "yes");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(authentication), authentication, null);
        }
    }

    private static void AddDestination(
        List<string> arguments,
        ConnectionEndpoint.Ssh endpoint)
    {
        if (endpoint.Username is not null)
        {
            arguments.Add("-l");
            arguments.Add(endpoint.Username);
        }

        arguments.Add("--");
        arguments.Add(endpoint.Host);
    }

    private static void AddOption(List<string> arguments, string name, string value)
    {
        arguments.Add("-o");
        arguments.Add($"{name}={value}");
    }

    private static string BuildRemoteLoginCommand(string directory)
    {
        if (directory.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "An SSH startup directory cannot contain a null character.",
                nameof(directory));
        }

        // OpenSSH joins the remote command into one string for the account's login shell.
        // The directory is passed as /bin/sh's positional parameter rather than interpolated
        // into its script, so whitespace, quotes, newlines, substitutions, and separators stay
        // data. The plan carries an explicit POSIX-target warning because Windows OpenSSH and
        // non-POSIX login shells do not guarantee this command contract.
        const string script = "cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l";
        var directoryArgument = directory.StartsWith("-", StringComparison.Ordinal)
            ? $"./{directory}"
            : directory;
        return $"exec /bin/sh -c {QuotePosixShellWord(script)} ghostshell-startup {QuotePosixShellWord(directoryArgument)}";
    }

    private static string QuotePosixShellWord(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string NullKnownHostsPath() =>
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
}
