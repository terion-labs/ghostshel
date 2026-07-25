using GhostShell.Core;

namespace GhostShell.Application;

internal static class AgentAuditEventId
{
    public static string ForPhase(
        AgentActionId actionId,
        AuditOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var digest = AgentActionDigest.FromUtf8(
            $"ghostshell-agent-audit-phase-v1\0{actionId.Value}\0{outcome}");
        return $"agent-{digest.Value}";
    }

    public static string ForPolicyTransition(
        AgentRunId runId,
        long policyGeneration)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        var digest = AgentActionDigest.FromUtf8(
            FormattableString.Invariant(
                $"ghostshell-agent-policy-transition-v1\0{runId.Value}\0{policyGeneration}"));
        return $"agent-policy-{digest.Value}";
    }
}
