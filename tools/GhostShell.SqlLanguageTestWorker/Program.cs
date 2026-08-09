using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

const int maximumFrameBytes = 8 * 1024 * 1024;
var mode = args.ElementAtOrDefault(0) ?? "normal";
var markerPath = args.ElementAtOrDefault(1);
long previousId = 0;
string? activeObjectName = null;
if (mode == "stderr")
{
    await Console.Error.WriteAsync(new string('!', 1024 * 1024));
    await Console.Error.FlushAsync();
}
if (mode == "environment" && markerPath is not null
    && Environment.GetEnvironmentVariable("GHOSTSHELL_SQL_LANGUAGE_TEST_SECRET") is not null)
{
    await File.WriteAllTextAsync(markerPath, "inherited");
}

while (true)
{
    var payload = await ReadFrameAsync(Console.OpenStandardInput());
    if (payload is null)
    {
        return;
    }

    using var request = JsonDocument.Parse(payload);
    var root = request.RootElement;
    var id = root.GetProperty("id").GetInt64();
    var method = root.GetProperty("method").GetString();
    if (root.GetProperty("version").GetInt32() != 1 || id <= previousId)
    {
        await WriteErrorAsync(id, "invalidEnvelope", Console.OpenStandardOutput());
        continue;
    }
    previousId = id;
    if (method == "shutdown")
    {
        await WriteJsonAsync(id, "{}", Console.OpenStandardOutput());
        return;
    }

    if (method == "initialize" || method == "updateCatalog")
    {
        var catalog = root.GetProperty("params").GetProperty("catalog");
        var firstObject = catalog.GetProperty("objects")[0];
        _ = catalog.GetProperty("driverId").GetString()
            ?? throw new InvalidDataException("driverId missing");
        var requestedObjectName = firstObject.GetProperty("id").GetProperty("name").GetString()
            ?? throw new InvalidDataException("object id missing");
        _ = firstObject.GetProperty("columns")[0].GetProperty("valueKind").GetString()
            ?? throw new InvalidDataException("column kind missing");
        if (mode == "init-error" && method == "initialize")
        {
            if (markerPath is not null)
            {
                var attempts = File.Exists(markerPath)
                    && int.TryParse(await File.ReadAllTextAsync(markerPath), out var parsed)
                        ? parsed
                        : 0;
                await File.WriteAllTextAsync(markerPath, (attempts + 1).ToString());
            }
            await Console.Error.WriteAsync("Babel token-list initialization failed.");
            await Console.Error.FlushAsync();
            await WriteErrorAsync(
                id,
                "internalError",
                Console.OpenStandardOutput(),
                "While building token lists");
            continue;
        }

        if (mode == "catalog-atomic" && method == "updateCatalog"
            && requestedObjectName == "reject")
        {
            await WriteErrorAsync(id, "invalidCatalog", Console.OpenStandardOutput());
            continue;
        }

        activeObjectName = requestedObjectName;
        await WriteJsonAsync(id, "{\"objectCount\":1}", Console.OpenStandardOutput());
        continue;
    }

    if (method == "complete" && mode == "crash-once" && markerPath is not null
        && !File.Exists(markerPath))
    {
        await File.WriteAllTextAsync(markerPath, "crashed");
        return;
    }

    if (method == "complete" && mode == "crash-twice" && markerPath is not null)
    {
        var crashCount = File.Exists(markerPath)
            && int.TryParse(await File.ReadAllTextAsync(markerPath), out var parsed)
                ? parsed
                : 0;
        if (crashCount < 2)
        {
            await File.WriteAllTextAsync(markerPath, (crashCount + 1).ToString());
            return;
        }
    }

    if (method == "complete" && mode == "crash-count" && markerPath is not null)
    {
        var crashCount = File.Exists(markerPath)
            && int.TryParse(await File.ReadAllTextAsync(markerPath), out var parsed)
                ? parsed
                : 0;
        await File.WriteAllTextAsync(markerPath, (crashCount + 1).ToString());
        return;
    }

    if (method == "complete" && mode == "catalog-atomic" && markerPath is not null
        && !File.Exists(markerPath))
    {
        await File.WriteAllTextAsync(markerPath, "crashed");
        return;
    }

    if (method == "complete" && mode == "crash")
    {
        return;
    }

    if (method == "complete" && mode == "hang")
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    if (method == "complete" && mode == "malformed")
    {
        await WriteFrameAsync("{not-json"u8.ToArray(), Console.OpenStandardOutput());
        continue;
    }

    if (method == "complete" && mode == "oversized")
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, maximumFrameBytes + 1);
        await Console.OpenStandardOutput().WriteAsync(prefix);
        await Console.OpenStandardOutput().FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    if (method == "complete" && mode == "operation-error")
    {
        await WriteErrorAsync(
            id,
            "internalError",
            Console.OpenStandardOutput(),
            "advisor failed");
        continue;
    }

    if (method == "complete" && mode == "invalid-params")
    {
        await WriteErrorAsync(id, "invalidParams", Console.OpenStandardOutput());
        continue;
    }

    var responseId = mode == "wrong-id" && method == "complete" ? id + 1 : id;
    var responseVersion = mode == "wrong-version" && method == "complete" ? 2 : 1;
    if (method == "complete")
    {
        var result = mode == "preferred-object"
            ? PreferredObjectCompletion(root.GetProperty("params"))
            : mode == "catalog-atomic"
            ? $$"""
                {"replacementStart":9,"replacementLength":2,"items":[
                  {"label":"{{activeObjectName}}","kind":"table","detail":"public","insertText":"{{activeObjectName}}"}
                ]}
                """
            : """
            {"replacementStart":9,"replacementLength":2,"items":[
              {"label":"name","kind":"column","detail":"VARCHAR","insertText":"name"},
              {"label":"people","kind":"table","detail":"public","insertText":"people"}
            ]}
            """;
        await WriteJsonAsync(
            responseId,
            result,
            Console.OpenStandardOutput(),
            responseVersion);
        continue;
    }

    if (method == "diagnose")
    {
        const string result = """
            {"items":[{"start":7,"length":7,"severity":"error",
              "message":"Column 'missing' not found","code":"unknownColumn"}]}
            """;
        await WriteJsonAsync(id, result, Console.OpenStandardOutput());
        continue;
    }

    await WriteErrorAsync(id, "methodNotFound", Console.OpenStandardOutput());
}

static string PreferredObjectCompletion(JsonElement parameters)
{
    var preferred = parameters.GetProperty("preferredObject");
    var catalog = preferred.GetProperty("catalog").GetString();
    var schema = preferred.GetProperty("schema").GetString();
    var name = preferred.GetProperty("name").GetString();
    var label = string.Join('.', new[] { catalog, schema, name }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    return $$"""
        {"replacementStart":0,"replacementLength":0,"items":[
          {"label":"{{label}}","kind":"table","detail":"preferred","insertText":"{{name}}"}
        ]}
        """;
}

static async Task<byte[]?> ReadFrameAsync(Stream input)
{
    var prefix = new byte[4];
    var first = await input.ReadAsync(prefix.AsMemory(0, 1));
    if (first == 0)
    {
        return null;
    }

    await input.ReadExactlyAsync(prefix.AsMemory(1, 3));
    var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
    var payload = new byte[length];
    await input.ReadExactlyAsync(payload);
    return payload;
}

static Task WriteJsonAsync(long id, string result, Stream output, int version = 1) =>
    WriteFrameAsync(
        Encoding.UTF8.GetBytes(
            $"{{\"version\":{version},\"id\":{id},\"result\":{result}}}"),
        output);

static Task WriteErrorAsync(
    long id,
    string code,
    Stream output,
    string message = "failure") =>
    WriteFrameAsync(
        Encoding.UTF8.GetBytes(
            $"{{\"version\":1,\"id\":{id},\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}"),
        output);

static async Task WriteFrameAsync(byte[] payload, Stream output)
{
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
    await output.WriteAsync(prefix);
    await output.WriteAsync(payload);
    await output.FlushAsync();
}
