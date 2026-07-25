using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>SFTP provider using SSH.NET behind a vendor-free filesystem seam.</summary>
public sealed class SftpFileProvider : RemoteHierarchicalFileProvider
{
    private static readonly FileProviderLimits ProviderLimits = new(
        maximumListPageSize: 1_000,
        maximumReadBytes: 64L * 1024 * 1024,
        maximumWriteBytes: 512L * 1024 * 1024,
        maximumTransferBytes: 512L * 1024 * 1024,
        maximumBufferSize: 1024 * 1024);

    public SftpFileProvider(
        ISecretVault secretVault,
        ISshHostKeyTrustStore knownHosts,
        SftpFileProviderOptions options)
        : this(
            new SshNetSftpSessionFactory(
                secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
                knownHosts ?? throw new ArgumentNullException(nameof(knownHosts)),
                options ?? throw new ArgumentNullException(nameof(options))),
            options)
    {
    }

    internal SftpFileProvider(
        IRemoteHierarchicalFileSessionFactory sessions,
        SftpFileProviderOptions options)
        : base(
            sessions,
            options.ProfileId,
            options.Authority,
            options.RemoteRoot,
            protocolName: "SFTP",
            allowBackslashSegments: true,
            FileNameComparison.CaseSensitive,
            options.ReconnectPolicy,
            ProviderLimits)
    {
        Connection = options.Connection;
        Diagnostics = options.Connection.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore
            ? [new SftpProviderDiagnostic(
                "sftp_host_key_verification_disabled",
                "SSH host-key verification is disabled for this connection profile.")]
            : [];
    }

    public ConnectionProfile Connection { get; }

    public IReadOnlyList<SftpProviderDiagnostic> Diagnostics { get; }
}
