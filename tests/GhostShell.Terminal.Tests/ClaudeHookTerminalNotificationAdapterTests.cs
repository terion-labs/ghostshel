using System.Text;
using System.Text.Json;
using GhostShell.Terminal;

namespace GhostShell.Terminal.Tests;

public sealed class ClaudeHookTerminalNotificationAdapterTests
{
    [Fact]
    public void Exposes_a_dedicated_private_process_switch()
    {
        Assert.Equal(
            "--claude-hook-terminal-notification",
            ClaudeHookTerminalNotificationAdapter.CommandLineSwitch);
    }

    [Theory]
    [InlineData("Stop", "Work complete")]
    [InlineData("StopFailure", "Work stopped with an error")]
    public void Writes_an_allowlisted_osc_777_response_for_completion_events(
        string eventName,
        string expectedBody)
    {
        var (exitCode, output) = Run(
            $$"""{"hook_event_name":"{{eventName}}"}""");

        Assert.Equal(0, exitCode);
        using var response = JsonDocument.Parse(output);
        Assert.True(response.RootElement.GetProperty("suppressOutput").GetBoolean());
        Assert.Equal(
            $"\u001b]777;notify;Claude Code;{expectedBody}\u0007",
            response.RootElement.GetProperty("terminalSequence").GetString());
        Assert.Equal(
            ["terminalSequence", "suppressOutput"],
            response.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public void Ignores_untrusted_hook_text_instead_of_copying_it_to_the_terminal()
    {
        const string injected =
            "private data;forged body\u001b]777;notify;Injected;Pwned\u0007";
        var input = JsonSerializer.Serialize(new
        {
            hook_event_name = "StopFailure",
            title = injected,
            message = injected,
            error = injected,
            last_assistant_message = injected,
        });
        var (_, output) = Run(input);

        using var response = JsonDocument.Parse(output);
        var terminalSequence = Assert.IsType<string>(response.RootElement
            .GetProperty("terminalSequence")
            .GetString());
        Assert.Equal(
            "\u001b]777;notify;Claude Code;Work stopped with an error\u0007",
            terminalSequence);
        Assert.DoesNotContain("private data", terminalSequence);
        Assert.Equal(1, terminalSequence.Count(character => character == '\u001b'));
        Assert.Equal(1, terminalSequence.Count(character => character == '\u0007'));
    }

    [Fact]
    public void Suppresses_stop_while_a_background_task_is_running()
    {
        var (_, output) = Run(
            """
            {
              "hook_event_name": "Stop",
              "background_tasks": [
                { "id": "task-1", "status": "completed" },
                { "id": "task-2", "status": "running" }
              ],
              "session_crons": []
            }
            """);

        Assert.Empty(output);
    }

    [Fact]
    public void Suppresses_stop_while_a_session_cron_remains()
    {
        var (_, output) = Run(
            """
            {
              "hook_event_name": "Stop",
              "background_tasks": [],
              "session_crons": [{ "id": "cron-1" }]
            }
            """);

        Assert.Empty(output);
    }

    [Fact]
    public void Completed_background_tasks_do_not_suppress_stop()
    {
        var (_, output) = Run(
            """
            {
              "hook_event_name": "Stop",
              "background_tasks": [
                { "id": "task-1", "status": "completed" },
                { "id": "task-2", "status": "failed" }
              ],
              "session_crons": []
            }
            """);

        Assert.NotEmpty(output);
    }

    [Fact]
    public void Stop_hook_recursion_flag_does_not_suppress_completion()
    {
        var (_, output) = Run(
            """
            {
              "hook_event_name": "Stop",
              "stop_hook_active": true,
              "background_tasks": [],
              "session_crons": []
            }
            """);

        Assert.NotEmpty(output);
    }

    [Fact]
    public void Background_work_does_not_hide_a_stop_failure()
    {
        var (_, output) = Run(
            """
            {
              "hook_event_name": "StopFailure",
              "background_tasks": [{ "status": "running" }],
              "session_crons": [{ "id": "cron-1" }]
            }
            """);

        Assert.NotEmpty(output);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"hook_event_name\":17}")]
    [InlineData("{\"hook_event_name\":\"Notification\"}")]
    [InlineData("{\"hook_event_name\":\"PreToolUse\"}")]
    public void Malformed_and_unhandled_inputs_fail_open_without_output(
        string input)
    {
        var (exitCode, output) = Run(input);

        Assert.Equal(0, exitCode);
        Assert.Empty(output);
    }

    [Fact]
    public void Oversized_input_fails_open_without_output()
    {
        var input = new string(
            'x',
            ClaudeHookTerminalNotificationAdapter.MaximumInputCharacters + 1);

        var (exitCode, output) = Run(input);

        Assert.Equal(0, exitCode);
        Assert.Empty(output);
    }

    [Fact]
    public void Accepts_a_long_stop_payload_at_the_input_boundary()
    {
        const string prefix =
            "{\"hook_event_name\":\"Stop\",\"last_assistant_message\":\"";
        const string suffix = "\"}";
        var input = prefix
            + new string(
                'x',
                ClaudeHookTerminalNotificationAdapter.MaximumInputCharacters
                - prefix.Length
                - suffix.Length)
            + suffix;

        var (exitCode, output) = Run(input);

        Assert.Equal(0, exitCode);
        Assert.NotEmpty(output);
    }

    [Fact]
    public void Reader_failure_fails_open_without_output()
    {
        var output = new StringWriter();

        var exitCode = ClaudeHookTerminalNotificationAdapter.Run(
            new ThrowingTextReader(),
            output);

        Assert.Equal(0, exitCode);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void Writer_failure_still_returns_a_success_exit_code()
    {
        var exitCode = ClaudeHookTerminalNotificationAdapter.Run(
            new StringReader("{\"hook_event_name\":\"Stop\"}"),
            new ThrowingTextWriter());

        Assert.Equal(0, exitCode);
    }

    private static (int ExitCode, string Output) Run(string input)
    {
        using var output = new StringWriter();
        var exitCode = ClaudeHookTerminalNotificationAdapter.Run(
            new StringReader(input),
            output);
        return (exitCode, output.ToString());
    }

    private sealed class ThrowingTextReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count) =>
            throw new IOException("Synthetic read failure.");
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value) =>
            throw new IOException("Synthetic write failure.");
    }
}
