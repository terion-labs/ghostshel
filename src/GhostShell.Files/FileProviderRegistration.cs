using GhostShell.Application;

namespace GhostShell.Files;

public sealed record FileProviderRegistration
{
    public FileProviderRegistration(
        string name,
        FileProviderFamily family,
        IFileProvider provider,
        FileLocation root)
        : this(
            name,
            family,
            provider,
            root,
            FilePanelCapability.None)
    {
    }

    public FileProviderRegistration(
        string name,
        FileProviderFamily family,
        IFileProvider provider,
        FileLocation root,
        FilePanelCapability governedMutationCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(root);
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, null);
        }

        if (provider.ProfileId != root.ProviderProfileId)
        {
            throw new ArgumentException(
                "A registered provider and its root must use the same profile ID.",
                nameof(root));
        }

        const FilePanelCapability knownGovernedMutationCapabilities =
            FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedDelete;
        if ((governedMutationCapabilities & ~knownGovernedMutationCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(governedMutationCapabilities),
                governedMutationCapabilities,
                "Only trusted governed-mutation capabilities may be registered here.");
        }

        if (governedMutationCapabilities.HasFlag(
                FilePanelCapability.GovernedCreateDirectory)
            && !provider.Capabilities.Supports(FileProviderCapability.CreateDirectory))
        {
            throw new ArgumentException(
                "Governed directory creation requires provider directory-creation support.",
                nameof(governedMutationCapabilities));
        }

        if (governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedDelete)
            && !provider.Capabilities.Supports(FileProviderCapability.Delete))
        {
            throw new ArgumentException(
                "Governed deletion requires provider deletion support.",
                nameof(governedMutationCapabilities));
        }

        Name = name.Trim();
        Family = family;
        Provider = provider;
        Root = root;
        GovernedMutationCapabilities = governedMutationCapabilities;
    }

    public string Name { get; }

    public FileProviderFamily Family { get; }

    public IFileProvider Provider { get; }

    public FileLocation Root { get; }

    /// <summary>
    /// Capabilities asserted only by trusted production composition after verifying that the
    /// provider confines mutations to its registered namespace and does not replay them.
    /// </summary>
    public FilePanelCapability GovernedMutationCapabilities { get; }
}
