namespace GhostShell.Application;

public interface ISecretAccessAuditSink
{
    ValueTask<SecretAccessAuditResult> AppendAsync(
        SecretAccessAuditRecord record,
        CancellationToken cancellationToken);
}
