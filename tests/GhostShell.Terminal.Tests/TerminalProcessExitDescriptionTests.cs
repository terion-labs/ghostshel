using GhostShell.Application;

namespace GhostShell.Terminal.Tests;

public sealed class TerminalProcessExitDescriptionTests
{
    public static TheoryData<string, string> KnownFailures => new()
    {
        {
            "ssh: connect to host private.example port 22: Operation timed out",
            "The connection attempt timed out."
        },
        {
            "ssh: connect to host private.example port 22: Connection refused",
            "The connection endpoint is offline or unreachable."
        },
        {
            "root@private.example: Permission denied (publickey).",
            "Connection authentication failed."
        },
        {
            "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!",
            "The remote host key changed."
        },
    };

    [Theory]
    [MemberData(nameof(KnownFailures))]
    public void KnownSshFailureUsesStableTextWithoutCopyingProcessOutput(
        string processOutput,
        string expected)
    {
        var description = TerminalProcessExitDescription.Describe(
            SshLaunch(),
            processOutput,
            exitCode: 255);

        Assert.Equal(expected, description);
        Assert.DoesNotContain("private.example", description, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSshFailureReportsOnlyTheExitCode()
    {
        var description = TerminalProcessExitDescription.Describe(
            SshLaunch(),
            "private failure text",
            exitCode: 42);

        Assert.Equal("The OpenSSH process exited with code 42.", description);
        Assert.DoesNotContain("private failure text", description, StringComparison.Ordinal);
    }

    private static TerminalLaunchRequest SshLaunch() => new(
        workingDirectory: null,
        executable: "/usr/bin/ssh",
        connectionMetadata: new TerminalConnectionMetadata(
            "SSH: root@private.example:22",
            initialWorkingDirectory: null));
}
