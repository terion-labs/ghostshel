namespace GhostShell.Application;

/// <summary>
/// Chooses whether ordinary browser panels share one in-memory application
/// profile or use the durable workspace identity as their partition key.
/// </summary>
public enum BrowserProfileSharing
{
    Shared,
    PerWorkspace,
}

public sealed record BrowserProfileSettings
{
    public BrowserProfileSettings(BrowserProfileSharing sharing)
    {
        if (!Enum.IsDefined(sharing))
        {
            throw new ArgumentOutOfRangeException(nameof(sharing));
        }

        Sharing = sharing;
    }

    public static BrowserProfileSettings Default { get; } = new(
        BrowserProfileSharing.Shared);

    public BrowserProfileSharing Sharing { get; }
}

/// <summary>
/// The live browser-profile preference. Changes affect browsers created after
/// the change; an open browser keeps the request context it started with.
/// </summary>
public interface IBrowserProfilePreferences
{
    BrowserProfileSettings Current { get; }

    event EventHandler? Changed;

    ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken);
}

/// <summary>
/// A logical Chromium context partition. Browser state is process-memory only;
/// the route is deliberately not part of this product identity because the CEF
/// host may shard one profile by route for its proxy preferences.
/// </summary>
public readonly record struct BrowserProfileKey
{
    private const int MaximumIdentityLength = 256;

    public BrowserProfileKey(BrowserProfileKind kind, string identity)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == BrowserProfileKind.Global)
        {
            if (!string.IsNullOrEmpty(identity))
            {
                throw new ArgumentException(
                    "The global browser profile has no secondary identity.",
                    nameof(identity));
            }
        }
        else if (string.IsNullOrWhiteSpace(identity)
            || identity.Length > MaximumIdentityLength)
        {
            throw new ArgumentException(
                $"A scoped browser profile identity must contain 1 to {MaximumIdentityLength} characters.",
                nameof(identity));
        }

        Kind = kind;
        Identity = identity;
    }

    public BrowserProfileKind Kind { get; }

    public string Identity { get; }

    public static BrowserProfileKey Global { get; } = new(
        BrowserProfileKind.Global,
        string.Empty);

    public static BrowserProfileKey ForWorkspace(string identity) => new(
        BrowserProfileKind.Workspace,
        identity);

    public static BrowserProfileKey ForWebApp(string identity) => new(
        BrowserProfileKind.WebApp,
        identity);
}

public enum BrowserProfileKind
{
    Global,
    Workspace,
    WebApp,
}

public enum BrowserProfileDataScope
{
    Global,
    Workspaces,
    WebApps,
    All,
}

public enum BrowserProfileClearStatus
{
    Cleared,
    InUse,
    Failed,
}

public sealed record BrowserProfileStorageUsage(
    long GlobalBytes,
    long WorkspaceBytes,
    long WebAppBytes)
{
    public long TotalBytes => checked(GlobalBytes + WorkspaceBytes + WebAppBytes);
}

public sealed record BrowserProfileClearResult
{
    public BrowserProfileClearResult(
        BrowserProfileClearStatus status,
        long clearedBytes,
        string message)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (clearedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clearedBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Status = status;
        ClearedBytes = clearedBytes;
        Message = message;
    }

    public BrowserProfileClearStatus Status { get; }

    public long ClearedBytes { get; }

    public string Message { get; }
}

/// <summary>
/// Reports legacy on-disk browser data and deliberately erases cookies, cache,
/// local storage, and other state held by a profile partition.
/// </summary>
public interface IBrowserProfileDataControl
{
    BrowserProfileStorageUsage ReadUsage();

    ValueTask<BrowserProfileClearResult> ClearAsync(
        BrowserProfileDataScope scope,
        CancellationToken cancellationToken);
}

public sealed class InMemoryBrowserProfilePreferences : IBrowserProfilePreferences
{
    private BrowserProfileSettings _current = BrowserProfileSettings.Default;

    public BrowserProfileSettings Current => _current;

    public event EventHandler? Changed;

    public ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}
