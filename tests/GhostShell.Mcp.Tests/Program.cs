using System.Text.Json;

namespace GhostShell.Mcp.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments is not ["--mcp-test-host", var mode, .. var hostArguments])
        {
            return 0;
        }

        await RunServerAsync(mode, hostArguments).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunServerAsync(string mode, string[] hostArguments)
    {
        if (mode == "lifecycle-marker"
            && hostArguments is [var startedPath, _, _])
        {
            await File.WriteAllTextAsync(
                    startedPath,
                    Environment.ProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        string? delayedListId = null;
        var unsupportedRequestAnswered = mode != "server-request";
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            if (root.TryGetProperty("method", out var methodProperty))
            {
                var method = methodProperty.GetString();
                if (method == "initialize")
                {
                    await WriteAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            protocolVersion = "2025-11-25",
                            capabilities = new
                            {
                                tools = new { listChanged = true },
                            },
                            serverInfo = new { name = "ghostshell-test", version = "1.0.0" },
                            instructions = mode == "oversized-instructions"
                                ? new string('i', 5 * 1024)
                                : null,
                        },
                    }).ConfigureAwait(false);
                }
                else if (method == "notifications/initialized")
                {
                    if (mode == "server-request")
                    {
                        await WriteAsync(new
                        {
                            jsonrpc = "2.0",
                            id = "unsupported-server-request",
                            method = "roots/list",
                            @params = new { },
                        }).ConfigureAwait(false);
                    }

                    if (mode == "stderr")
                    {
                        await Console.Error.WriteAsync(
                                "LEAK-ME-NOT:" + new string('s', 2048) + "\n")
                            .ConfigureAwait(false);
                        await Console.Error.FlushAsync().ConfigureAwait(false);
                    }
                }
                else if (method == "tools/list")
                {
                    var id = root.GetProperty("id").GetRawText();
                    if (mode == "control-message-flood")
                    {
                        for (var index = 0; index < 64; index++)
                        {
                            await WriteAsync(new
                            {
                                jsonrpc = "2.0",
                                method = "notifications/tools/list_changed",
                            }).ConfigureAwait(false);
                        }
                    }
                    else if (mode == "hang-list")
                    {
                        continue;
                    }
                    else if (!unsupportedRequestAnswered)
                    {
                        delayedListId = id;
                    }
                    else
                    {
                        await WriteToolListAsync(mode, id).ConfigureAwait(false);
                    }
                }
                else if (method == "tools/call")
                {
                    await WriteToolCallAsync(
                            mode,
                            hostArguments,
                            root.GetProperty("id").GetRawText(),
                            root.GetProperty("params"))
                        .ConfigureAwait(false);
                }
            }
            else if (mode == "server-request"
                && root.TryGetProperty("id", out var responseId)
                && responseId.GetString() == "unsupported-server-request"
                && root.TryGetProperty("error", out var error)
                && error.GetProperty("code").GetInt32() == -32601)
            {
                unsupportedRequestAnswered = true;
                if (delayedListId is not null)
                {
                    await WriteToolListAsync(mode, delayedListId).ConfigureAwait(false);
                    delayedListId = null;
                }
            }
        }

        if (mode == "lifecycle-marker"
            && hostArguments is [_, var closedPath, _])
        {
            await File.WriteAllTextAsync(
                    closedPath,
                    "closed")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteToolListAsync(string mode, string rawId)
    {
        using var idDocument = JsonDocument.Parse(rawId);
        var id = idDocument.RootElement.Clone();
        if (mode == "oversized-message")
        {
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "echo",
                            description = new string('x', 2048),
                            inputSchema = new { type = "object" },
                        },
                    },
                },
            }).ConfigureAwait(false);
            return;
        }

        if (mode == "duplicate-property")
        {
            await WriteRawAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":" + rawId
                    + ",\"result\":{\"tools\":[],\"tools\":[]}}")
                .ConfigureAwait(false);
            return;
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
        };
        if (mode == "oversized-schema")
        {
            schema.Add("description", new string('x', 512));
        }
        else if (mode == "aggregate-schema-limit")
        {
            schema.Add(
                "properties",
                new
                {
                    value = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            new string('s', 58 * 1024),
                        },
                    },
                });
        }

        var tools = new List<object>
        {
            new
            {
                name = "control",
                title = mode == "oversized-title"
                    ? new string('t', 2 * 1024)
                    : "Control",
                description = mode == "oversized-description"
                    ? new string('d', 5 * 1024)
                    : "Test tool",
                inputSchema = schema,
            },
        };
        if (mode == "many-tools")
        {
            tools.Add(new
            {
                name = "second",
                title = "Second",
                description = "Second test tool",
                inputSchema = schema,
            });
        }
        else if (mode == "many-unselected-tools")
        {
            for (var index = 1; index < 65; index++)
            {
                tools.Add(new
                {
                    name = $"unselected-{index}",
                    title = $"Unselected {index}",
                    description = "Unselected test tool",
                    inputSchema = schema,
                });
            }
        }
        else if (mode == "aggregate-schema-limit")
        {
            for (var index = 1; index < 10; index++)
            {
                tools.Add(new
                {
                    name = $"schema-{index}",
                    title = $"Schema {index}",
                    description = "Large valid schema",
                    inputSchema = schema,
                });
            }
        }
        else if (mode == "secret-tool-name")
        {
            tools.Add(new
            {
                name = Environment.GetEnvironmentVariable(
                    "GHOSTSHELL_REFLECTED_TOOL"),
                title = "Reflected",
                description = "Server-controlled identifier",
                inputSchema = schema,
            });
        }

        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            result = new { tools },
        }).ConfigureAwait(false);
    }

    private static async Task WriteToolCallAsync(
        string mode,
        string[] hostArguments,
        string rawId,
        JsonElement requestParams)
    {
        if (mode == "call-marker" && hostArguments is [var markerPath])
        {
            await File.WriteAllTextAsync(
                    markerPath,
                    "tool-called")
                .ConfigureAwait(false);
        }
        else if (mode == "lifecycle-marker"
            && hostArguments is [_, _, var calledPath])
        {
            await File.WriteAllTextAsync(
                    calledPath,
                    "tool-called")
                .ConfigureAwait(false);
        }

        if (requestParams.TryGetProperty("arguments", out var arguments)
            && arguments.TryGetProperty("hang", out var hang)
            && hang.ValueKind == JsonValueKind.True)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }

        object structured = mode switch
        {
            "environment" => new
            {
                inherited = Environment.GetEnvironmentVariable(hostArguments[0]),
                allowed = Environment.GetEnvironmentVariable("GHOSTSHELL_ALLOWED"),
            },
            "arguments" => new { value = hostArguments[0] },
            _ => new { pid = Environment.ProcessId },
        };

        using var idDocument = JsonDocument.Parse(rawId);
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id = idDocument.RootElement.Clone(),
            result = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = mode == "oversized-result"
                            ? new string('r', 2048)
                            : "ok",
                    },
                },
                structuredContent = structured,
                isError = false,
            },
        }).ConfigureAwait(false);
    }

    private static Task WriteAsync<T>(T value) =>
        WriteRawAsync(JsonSerializer.Serialize(value));

    private static async Task WriteRawAsync(string json)
    {
        await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
