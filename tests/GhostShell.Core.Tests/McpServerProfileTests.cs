using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class McpServerProfileTests
{
    [Fact]
    public void ProfileRoundTripsASeparateExecutableArgvAndOpaqueEnvironmentReferences()
    {
        var secret = new SecretRef("vault-mcp-github-token");
        var arguments = new[] { "--transport", "stdio" };
        var environment = new[]
        {
            new McpServerEnvironmentVariable("Z_TOKEN", secret),
            new McpServerEnvironmentVariable(
                "A_TOKEN",
                new SecretRef("vault-mcp-secondary-token")),
        };
        var enabledTools = new[] { "repositories.read", "issues.list" };
        var profile = new McpServerProfile(
            new McpServerProfileId("mcp.github"),
            McpServerProfile.CurrentSchemaVersion,
            "GitHub tools",
            "/usr/local/bin/github-mcp-server",
            arguments,
            "/srv/ghostshell",
            environment,
            enabledTools,
            isEnabled: false);

        arguments[0] = "--changed";
        environment[0] = new McpServerEnvironmentVariable(
            "REPLACED",
            new SecretRef("vault-replaced"));
        enabledTools[0] = "changed";
        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<McpServerProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(McpServerProfile.Kind, restored.Key.Kind);
        Assert.Equal(["--transport", "stdio"], restored.Arguments);
        Assert.Equal(["A_TOKEN", "Z_TOKEN"], restored.Environment.Select(item => item.Name));
        Assert.Equal(
            ["issues.list", "repositories.read"],
            restored.EnabledTools);
        Assert.Equal(secret, restored.Environment[1].Reference);
        Assert.IsType<McpServerTransport.Stdio>(restored.Transport);
        Assert.False(restored.IsEnabled);
        Assert.Contains("\"$type\":\"stdio\"", json, StringComparison.Ordinal);
        Assert.Contains(secret.Value, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secretValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateEnvironmentNamesAndEnabledToolsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            environment:
            [
                new("TOKEN", new SecretRef("vault-one")),
                new("token", new SecretRef("vault-two")),
            ]));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            enabledTools: ["issues.list", "issues.list"]));
    }

    [Fact]
    public void ArgvMayRepeatValuesButRemainsBounded()
    {
        var repeated = CreateProfile(arguments: ["--verbose", "--verbose"]);

        Assert.Equal(["--verbose", "--verbose"], repeated.Arguments);
        Assert.Throws<ArgumentException>(() => CreateProfile(
            arguments: Enumerable
                .Repeat("x", McpServerProfile.MaximumArgumentCount + 1)
                .ToArray()));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            arguments: Enumerable
                .Repeat(
                    new string('x', McpServerProfile.MaximumArgumentBytes),
                    McpServerProfile.MaximumArgumentsBytes
                    / McpServerProfile.MaximumArgumentBytes
                    + 1)
                .ToArray()));
    }

    [Theory]
    [InlineData("name", "MCP\nserver")]
    [InlineData("executable", " server")]
    [InlineData("working-directory", "/srv/\u202Ehidden")]
    [InlineData("tool", "issues\u2028list")]
    public void ConfigurationTextMustBeValidBoundedPrintableUnicode(
        string field,
        string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => field switch
        {
            "name" => CreateProfile(name: invalidValue),
            "executable" => CreateProfile(executable: invalidValue),
            "argument" => CreateProfile(arguments: [invalidValue]),
            "working-directory" => CreateProfile(workingDirectory: invalidValue),
            "tool" => CreateProfile(enabledTools: [invalidValue]),
            _ => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void ConfigurationRejectsUnpairedUtf16Surrogates()
    {
        var invalidUnicode = new string('\uD800', 1);

        Assert.Throws<ArgumentException>(() => CreateProfile(arguments: [invalidUnicode]));
    }

    [Fact]
    public void SchemaCollectionsAndSecretReferencesAreStrictlyBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpServerProfile(
            new McpServerProfileId("mcp.server"),
            McpServerProfile.CurrentSchemaVersion + 1,
            "Server",
            "server",
            [],
            null,
            [],
            []));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            executable: new string('x', McpServerProfile.MaximumExecutableBytes + 1)));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            environment: Enumerable
                .Range(0, McpServerProfile.MaximumEnvironmentVariableCount + 1)
                .Select(index => new McpServerEnvironmentVariable(
                    $"TOKEN_{index}",
                    new SecretRef($"vault-{index}")))
                .ToArray()));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            enabledTools: Enumerable
                .Range(0, McpServerProfile.MaximumEnabledToolCount + 1)
                .Select(index => $"tool-{index}")
                .ToArray()));
        Assert.Throws<ArgumentException>(() => new McpServerEnvironmentVariable(
            "TOKEN",
            new SecretRef(
                new string('r', McpServerProfile.MaximumSecretReferenceBytes + 1))));
    }

    [Theory]
    [MemberData(nameof(LiteralCredentialArguments))]
    public void ArgumentsRejectLiteralCredentials(IReadOnlyList<string> arguments)
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(arguments: arguments));
    }

    public static TheoryData<IReadOnlyList<string>> LiteralCredentialArguments =>
        new()
        {
            { new[] { "--token", "credential-value" } },
            { new[] { "--token=sk-1234567890abcdef" } },
            { new[] { "--api-key", "credential-value" } },
            { new[] { "authorization: bearer credential-value" } },
        };

    [Fact]
    public void EnvironmentRejectsSecretShapedVaultReferences()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            environment:
            [
                new(
                    "TOKEN",
                    new SecretRef("sk-1234567890abcdef")),
            ]));
    }

    [Theory]
    [InlineData("issues list")]
    [InlineData("issues/list")]
    [InlineData("issues\u00E9")]
    public void EnabledToolsRequirePortableProtocolIdentifiers(string toolName)
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            enabledTools: [toolName]));
    }

    [Fact]
    public void EnabledToolNamesRemainBounded()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            enabledTools:
            [
                new string(
                    't',
                    McpServerProfile.MaximumToolNameBytes + 1),
            ]));
    }

    [Fact]
    public void StreamableHttpRoundTripsEndpointAndOpaqueHeaderReferences()
    {
        var authorization = new SecretRef("vault-mcp-authorization");
        var headers = new[]
        {
            new McpServerHttpHeader("X-Tenant", new SecretRef("vault-tenant")),
            new McpServerHttpHeader("Authorization", authorization),
        };
        var profile = new McpServerProfile(
            new McpServerProfileId("mcp.remote"),
            McpServerProfile.CurrentSchemaVersion,
            "Remote MCP",
            new McpServerTransport.StreamableHttp(
                new Uri("https://mcp.example.test/rpc"),
                headers),
            ["issues.list"]);

        headers[0] = new McpServerHttpHeader(
            "X-Replaced",
            new SecretRef("vault-replaced"));
        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<McpServerProfile>(json);

        var transport = Assert.IsType<McpServerTransport.StreamableHttp>(
            restored!.Transport);
        Assert.Equal("https://mcp.example.test/rpc", transport.Endpoint.AbsoluteUri);
        Assert.Equal(
            ["Authorization", "X-Tenant"],
            transport.Headers.Select(header => header.Name));
        Assert.Equal(authorization, transport.Headers[0].Reference);
        Assert.False(transport.AllowInsecureTransport);
        Assert.Contains(
            "\"$type\":\"streamable-http\"",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://mcp.example.test/rpc")]
    [InlineData("https://user@mcp.example.test/rpc")]
    [InlineData("https://mcp.example.test/rpc#fragment")]
    public void StreamableHttpRejectsUnsafeEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            new McpServerTransport.StreamableHttp(
                new Uri(endpoint),
                []));
    }

    [Fact]
    public void PlaintextStreamableHttpRequiresExplicitAcknowledgement()
    {
        var transport = new McpServerTransport.StreamableHttp(
            new Uri("http://127.0.0.1:3000/mcp"),
            [],
            allowInsecureTransport: true);

        Assert.True(transport.AllowInsecureTransport);
    }

    [Theory]
    [InlineData("http://mcp.example.test/rpc")]
    [InlineData("http://192.168.1.20:3000/mcp")]
    public void PlaintextStreamableHttpRejectsNonLoopbackEvenWhenAcknowledged(
        string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            new McpServerTransport.StreamableHttp(
                new Uri(endpoint),
                [],
                allowInsecureTransport: true));
    }

    [Theory]
    [InlineData("Accept")]
    [InlineData("Content-Type")]
    [InlineData("MCP-Session-Id")]
    [InlineData("MCP-Protocol-Version")]
    [InlineData("Bad Header")]
    public void StreamableHttpRejectsReservedOrInvalidHeaderNames(
        string name)
    {
        Assert.Throws<ArgumentException>(() => new McpServerHttpHeader(
            name,
            new SecretRef("vault-header")));
    }

    [Fact]
    public void StreamableHttpRejectsDuplicateHeaderNamesIgnoringCase()
    {
        Assert.Throws<ArgumentException>(() =>
            new McpServerTransport.StreamableHttp(
                new Uri("https://mcp.example.test/rpc"),
                [
                    new McpServerHttpHeader(
                        "Authorization",
                        new SecretRef("vault-one")),
                    new McpServerHttpHeader(
                        "authorization",
                        new SecretRef("vault-two")),
                ]));
    }

    private static McpServerProfile CreateProfile(
        string name = "Server",
        string executable = "server",
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyList<McpServerEnvironmentVariable>? environment = null,
        IReadOnlyList<string>? enabledTools = null) =>
        new(
            new McpServerProfileId("mcp.server"),
            McpServerProfile.CurrentSchemaVersion,
            name,
            executable,
            arguments ?? [],
            workingDirectory,
            environment ?? [],
            enabledTools ?? []);
}
