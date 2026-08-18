namespace GhostShell.Files;

/// <summary>
/// Keeps recently verified SFTP metadata inside one authenticated session. Directory listings
/// provide exact entry metadata, so child navigation can avoid repeating remote path probes.
/// </summary>
internal sealed class SftpMetadataCache(
    TimeProvider timeProvider,
    TimeSpan lifetime,
    int maximumEntries)
{
    private readonly Dictionary<string, CachedEntry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string path, out RemoteFileEntry? entry)
    {
        var normalized = Normalize(path);
        if (!_entries.TryGetValue(normalized, out var cached))
        {
            entry = null;
            return false;
        }

        if (timeProvider.GetUtcNow() - cached.StoredAt > lifetime)
        {
            _entries.Remove(normalized);
            entry = null;
            return false;
        }

        entry = cached.Entry;
        return true;
    }

    public void Store(string path, RemoteFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureCapacity(1);
        _entries[Normalize(path)] = new CachedEntry(entry, timeProvider.GetUtcNow());
    }

    public void StoreDirectory(
        string path,
        IReadOnlyList<RemoteFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var directory = Normalize(path);
        RemoveDirectChildren(directory);
        if (entries.Count > maximumEntries)
        {
            return;
        }

        EnsureCapacity(entries.Count);
        var storedAt = timeProvider.GetUtcNow();
        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            _entries[Child(directory, entry.Name)] = new CachedEntry(entry, storedAt);
        }
    }

    public void Clear() => _entries.Clear();

    private void EnsureCapacity(int incoming)
    {
        if (incoming > maximumEntries)
        {
            return;
        }

        if (_entries.Count + incoming > maximumEntries)
        {
            _entries.Clear();
        }
    }

    private void RemoveDirectChildren(string directory)
    {
        foreach (var path in _entries.Keys.Where(path => string.Equals(Parent(path), directory, StringComparison.Ordinal)).ToArray())
        {
            _entries.Remove(path);
        }
    }

    private static string Child(string parent, string name) =>
        string.Equals(parent, "/", StringComparison.Ordinal)
            ? $"/{name}"
            : $"{parent}/{name}";

    private static string Parent(string path)
    {
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Normalize(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;

    private sealed record CachedEntry(
        RemoteFileEntry Entry,
        DateTimeOffset StoredAt);
}
