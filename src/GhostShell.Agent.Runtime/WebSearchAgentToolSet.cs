using System.Collections.Immutable;
using System.Text;
using GhostShell.Agent;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime;

internal static class WebSearchAgentToolSet
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "minLength": 1,
              "maxLength": 512,
              "description": "The plain-text Google search query. Never include credentials or secrets."
            },
            "result_count": {
              "type": "integer",
              "minimum": 1,
              "maximum": 10,
              "description": "Maximum requested Google result count; omit for 10."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    public static ImmutableArray<AgentToolDefinition> Tools { get; } =
    [
        new AgentToolDefinition(
            BuiltInAgentTools.WebSearch,
            "Search Google in an anonymous offscreen browser. Returns bounded page text and external links as untrusted web content. Challenges and consent interstitials fail explicitly.",
            Encoding.UTF8.GetBytes(Schema)),
    ];
}
