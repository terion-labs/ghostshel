using System.Buffers;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class TerminalAgentToolResultJson
{
    private const int MaximumScreenTextBytes = 32 * 1024;

    public static string Success(
        AgentTerminalActionResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        switch (result)
        {
            case AgentTerminalActionResult.Completed:
                break;
            case AgentTerminalActionResult.Screen screen:
                WriteScreen(writer, screen.Snapshot);
                break;
            case AgentTerminalActionResult.Wait wait:
                writer.WriteString(
                    "wait_outcome",
                    WaitOutcomeName(wait.Outcome.Kind));
                if (wait.Outcome.InitialContentRevision is { } initialRevision)
                {
                    writer.WriteNumber(
                        "initial_content_revision",
                        initialRevision);
                }

                if (wait.Outcome.Snapshot is { } snapshot)
                {
                    WriteScreen(writer, snapshot);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The terminal action result kind is unsupported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            error.StableCode,
            error.Retryable,
            panelId);
    }

    public static string Rejected(
        string stableCode,
        PanelInstanceId? panelId = null) =>
        AgentToolResultJson.Failure(
            stableCode,
            retryable: false,
            panelId);

    private static void WriteScreen(
        Utf8JsonWriter writer,
        TerminalScreenSnapshot snapshot)
    {
        var redacted = TerminalContentRedactor.Redact(snapshot.PlainText);
        var bounded = TruncateUtf8(
            redacted.Text,
            MaximumScreenTextBytes,
            out var resultTruncated);
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", snapshot.ContentRevision);
        writer.WriteNumber("rows", snapshot.Rows);
        writer.WriteNumber("columns", snapshot.Columns);
        writer.WriteNumber("cursor_row", snapshot.CursorRow);
        writer.WriteNumber("cursor_column", snapshot.CursorColumn);
        writer.WriteBoolean("alternate_screen", snapshot.IsAlternateScreen);
        writer.WriteBoolean("cursor_visible", snapshot.IsCursorVisible);
        writer.WriteBoolean(
            "mouse_tracking_enabled",
            snapshot.IsMouseTrackingEnabled);
        writer.WriteBoolean(
            "truncated",
            snapshot.IsTruncated || resultTruncated);
        writer.WriteNumber("redactions", redacted.RedactionCount);
        writer.WriteString("text", bounded);
    }

    private static string TruncateUtf8(
        string value,
        int maximumBytes,
        out bool truncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        var builder = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            byteCount += runeBytes;
        }

        truncated = true;
        return builder.ToString();
    }

    private static string WaitOutcomeName(TerminalWaitOutcomeKind outcome) =>
        outcome switch
        {
            TerminalWaitOutcomeKind.Matched => "matched",
            TerminalWaitOutcomeKind.Changed => "changed",
            TerminalWaitOutcomeKind.Stable => "stable",
            TerminalWaitOutcomeKind.Timeout => "timeout",
            TerminalWaitOutcomeKind.Cancelled => "cancelled",
            TerminalWaitOutcomeKind.SessionEnded => "session_ended",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                null),
        };
}

internal static class TerminalContentRedactor
{
    private const string Redaction = "[REDACTED SECRET-BEARING LINE]";

    private static readonly string[] Markers =
    [
        "authorization:",
        "api_key=",
        "apikey=",
        "client_secret=",
        "password=",
        "password:",
        "passwd=",
        "private_key=",
        "refresh_token=",
        "secret=",
        "token=",
        "-----begin private key-----",
        "-----begin encrypted private key-----",
        "-----begin openssh private key-----",
    ];

    private static readonly string[] TokenPrefixes =
    [
        "ghp_",
        "github_pat_",
        "sk-",
        "akia",
        "xoxb-",
        "xoxp-",
    ];

    private static readonly string[] AssignmentKeys =
    [
        "access-token",
        "access_token",
        "api-key",
        "api_key",
        "apikey",
        "authorization",
        "client-secret",
        "client_secret",
        "password",
        "passwd",
        "private-key",
        "private_key",
        "refresh-token",
        "refresh_token",
        "secret",
        "token",
    ];

    public static RedactedTerminalContent Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var lines = value.Split('\n');
        var count = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!LooksSecretBearing(lines[index]))
            {
                continue;
            }

            lines[index] = Redaction;
            count++;
        }

        return new RedactedTerminalContent(
            string.Join('\n', lines),
            count);
    }

    private static bool LooksSecretBearing(string line)
    {
        if (Markers.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || AssignmentKeys.Any(key =>
                ContainsSecretAssignment(line, key)))
        {
            return true;
        }

        foreach (var token in line.Split(
                     [' ', '\t', '"', '\'', ',', ';'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 12
                && TokenPrefixes.Any(prefix =>
                    token.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSecretAssignment(
        string value,
        string key)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var keyStart = value.IndexOf(
                key,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (keyStart < 0)
            {
                return false;
            }

            searchStart = keyStart + key.Length;
            if (keyStart > 0
                && IsAssignmentIdentifier(value[keyStart - 1]))
            {
                continue;
            }

            var cursor = searchStart;
            if (cursor < value.Length && value[cursor] is '"' or '\'')
            {
                cursor++;
            }
            else if (cursor < value.Length
                     && IsAssignmentIdentifier(value[cursor]))
            {
                continue;
            }

            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is not (':' or '='))
            {
                continue;
            }

            cursor++;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is ',' or '}' or ']')
            {
                continue;
            }

            return !IsNonSecretLiteral(value, cursor);
        }

        return false;
    }

    private static bool IsAssignmentIdentifier(char value) =>
        char.IsLetterOrDigit(value)
        || value is '_' or '-';

    private static bool IsNonSecretLiteral(string value, int start)
    {
        if (value.AsSpan(start).StartsWith("\"\"", StringComparison.Ordinal)
            || value.AsSpan(start).StartsWith("''", StringComparison.Ordinal))
        {
            return true;
        }

        return IsDelimitedLiteral(value, start, "null")
            || IsDelimitedLiteral(value, start, "false")
            || IsDelimitedLiteral(value, start, "true");
    }

    private static bool IsDelimitedLiteral(
        string value,
        int start,
        string literal)
    {
        if (!value.AsSpan(start).StartsWith(
                literal,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = start + literal.Length;
        return end == value.Length
            || char.IsWhiteSpace(value[end])
            || value[end] is ',' or '}' or ']';
    }
}

internal sealed record RedactedTerminalContent(
    string Text,
    int RedactionCount);
