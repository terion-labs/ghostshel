using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhostShell.Terminal;

/// <summary>
/// Converts supported Claude Code completion hooks into terminal-native
/// notifications without exposing hook content to the terminal or OS.
/// </summary>
public static class ClaudeHookTerminalNotificationAdapter
{
    public const string CommandLineSwitch =
        "--claude-hook-terminal-notification";

    internal const int MaximumInputCharacters = 4 * 1024 * 1024;

    private const string NotificationTitle = "Claude Code";
    private const string StopBody = "Work complete";
    private const string StopFailureBody = "Work stopped with an error";

    /// <summary>
    /// Reads one Claude hook payload and writes a hook response when the event
    /// is supported. Every path returns success so this optional notification
    /// bridge can never reject or interrupt Claude's work.
    /// </summary>
    public static int Run(TextReader input, TextWriter output)
    {
        try
        {
            var inputJson = ReadBounded(input);
            if (inputJson is null
                || !TryCreateResponse(inputJson, out var response))
            {
                return 0;
            }

            output.Write(JsonSerializer.Serialize(response));
        }
        catch (Exception)
        {
            // A notification hook is observational. Reader, parser, serializer,
            // and writer failures must all fail open rather than block Claude.
        }

        return 0;
    }

    private static string? ReadBounded(TextReader input)
    {
        var buffer = new char[4 * 1024];
        var inputJson = new StringBuilder(buffer.Length);
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read is 0)
            {
                break;
            }

            if (inputJson.Length > MaximumInputCharacters - read)
            {
                return null;
            }

            inputJson.Append(buffer, 0, read);
        }

        return inputJson.ToString();
    }

    private static bool TryCreateResponse(
        string inputJson,
        out ClaudeHookResponse response)
    {
        response = default!;
        try
        {
            using var document = JsonDocument.Parse(
                inputJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("hook_event_name", out var eventElement)
                || eventElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var body = eventElement.GetString() switch
            {
                "Stop" when !HasActiveBackgroundWork(root) => StopBody,
                "StopFailure" => StopFailureBody,
                _ => null,
            };
            if (body is null)
            {
                return false;
            }

            response = new ClaudeHookResponse(
                BuildOsc777Notification(NotificationTitle, body),
                SuppressOutput: true);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasActiveBackgroundWork(JsonElement root)
    {
        if (root.TryGetProperty("session_crons", out var crons)
            && crons.ValueKind == JsonValueKind.Array
            && crons.GetArrayLength() > 0)
        {
            return true;
        }

        if (!root.TryGetProperty("background_tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Object
                && task.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(
                    status.GetString(),
                    "running",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildOsc777Notification(string title, string body) =>
        $"\u001b]777;notify;{title};{body}\u0007";

    private sealed record ClaudeHookResponse(
        [property: JsonPropertyName("terminalSequence")]
        string TerminalSequence,
        [property: JsonPropertyName("suppressOutput")]
        bool SuppressOutput);
}
