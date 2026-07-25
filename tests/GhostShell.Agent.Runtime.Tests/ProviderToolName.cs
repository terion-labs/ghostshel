using GhostShell.Agent;

namespace GhostShell.Agent.Runtime.Tests;

internal static class ProviderToolName
{
    public static string FromInternal(string internalName) =>
        new AgentToolDefinition(
            internalName,
            "Test tool.",
            "{\"type\":\"object\"}"u8.ToArray()).ProviderName;
}
