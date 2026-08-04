using System.Security.Cryptography;
using GhostShell.Application;
using LiteDB;

namespace GhostShell.Files;

/// <summary>
/// Where downloaded preview content lives: in memory below the auto-load
/// threshold, and in a LiteDB blob container above it. One engine, two
/// lifetimes.
///
/// The session container is always encrypted, with a key drawn from the
/// CSPRNG and held only in this object — deleting the file on exit is
/// hygiene, but the guarantee is that the key dies with the process, so even
/// a container orphaned by a crash is unreadable noise. The persistent
/// container exists only while "keep previews between runs" is on.
///
/// Orphans from crashed runs are swept on construction. The sweep never
/// guesses from file age: each live session holds an exclusive lock on a
/// companion .lock file, and only a container whose lock can be taken is
/// dead. Deleting first and asking later would work on Windows, where open
/// files refuse deletion, and quietly destroy a live sibling's cache on
/// POSIX, where unlink always succeeds.
/// </summary>
public sealed class PreviewContentCache : IPreviewCacheControl, IDisposable
{
    private const string DirectoryName = "ghostshell-preview-cache";
    private const string PersistentFileName = "store.db";

    /// <summary>
    /// The previous design's directory of plaintext downloaded copies. Swept
    /// on every start so no machine keeps remote file contents in the clear
    /// from before this cache existed.
    /// </summary>
    private const string LegacyPlaintextDirectoryName = "ghostshell-file-cache";

    /// <summary>
    /// The most memory the in-memory tier may hold across entries. Small next
    /// to the disk budget because these are duplicated in RAM per hit anyway.
    /// </summary>
    private const long MemoryBudgetBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _sessionId = Guid.NewGuid().ToString("n");
    private readonly IFilePreviewPreferences? _preferences;
    private readonly Dictionary<string, byte[]> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _memoryOrder = [];
    private long _memoryBytes;
    private FileStream? _sessionLock;
    private LiteDatabase? _session;
    private LiteDatabase? _persistent;
    private bool _disposed;

    public PreviewContentCache(
        IFilePreviewPreferences? preferences = null,
        string? directory = null)
    {
        _preferences = preferences;
        _directory = directory
            ?? Path.Combine(Path.GetTempPath(), DirectoryName);
        Directory.CreateDirectory(_directory);
        SweepLegacyPlaintextCache();
        SweepDeadSessions();
        if (_preferences is not null)
        {
            _preferences.Changed += OnPreferencesChanged;
        }

        if (Keep is false)
        {
            // Persistence was turned off in some earlier run; whatever that
            // run left behind was written under a promise no longer made.
            DeletePersistentContainer();
        }
    }

    private bool Keep => _preferences?.Current.KeepPreviewsBetweenRuns
        ?? FilePreviewSettings.Default.KeepPreviewsBetweenRuns;

    private long Budget => _preferences?.Current.CacheBudgetBytes
        ?? FilePreviewSettings.Default.CacheBudgetBytes;

    private long MemoryThreshold => _preferences?.Current.AutoLoadThresholdBytes
        ?? FilePreviewSettings.Default.AutoLoadThresholdBytes;

    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (var name in new[] { SessionPath, PersistentPath })
                {
                    try
                    {
                        var file = new FileInfo(name);
                        if (file.Exists)
                        {
                            bytes += file.Length;
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                return bytes;
            }
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _memory.Clear();
            _memoryOrder.Clear();
            _memoryBytes = 0;
            _session?.Dispose();
            _session = null;
            TryDelete(SessionPath);
            DeletePersistentContainer();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The content stored under this key, or null. Never stale: the key
    /// encodes the file's version, so a changed file is a different key.
    /// </summary>
    public FilePreviewContent? TryGet(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_memory.TryGetValue(key, out var bytes))
            {
                _memoryOrder.Remove(key);
                _memoryOrder.AddFirst(key);
                return new MemoryContent(bytes);
            }

            foreach (var container in Containers())
            {
                try
                {
                    var storage = container.GetStorage<string>();
                    var entry = storage.FindById(key);
                    // An upload the process died inside leaves an entry whose
                    // recorded length disagrees with its chunks; reading it
                    // would serve a torn file as a whole one.
                    if (entry is not null
                        && entry.Metadata.TryGetValue("whole", out var whole)
                        && whole.AsBoolean)
                    {
                        return new ContainerContent(this, container, key, entry.Length);
                    }
                }
                catch (LiteException)
                {
                    // A cache that cannot answer is a miss, never a failure.
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Opens a destination for content about to be downloaded. The bytes are
    /// written as they arrive — a large file streams straight into its
    /// container, never held whole in memory — and become readable only once
    /// <see cref="PendingContent.Commit"/> says every byte did arrive. A
    /// pending put dropped without committing leaves nothing readable.
    ///
    /// Small content (by the size hint) goes to the in-memory tier. Large
    /// content goes to the persistent container when previews are kept
    /// between runs, else to the encrypted session container. Content with no
    /// key — no version identity to cache under — is never kept: small stays
    /// transient in memory, large goes to the session container under a
    /// one-time name that is not findable again.
    /// </summary>
    public PendingContent BeginPut(string? key, long? sizeHint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sizeHint is { } size && size <= MemoryThreshold && size <= MemoryBudgetBytes)
            {
                return new PendingContent(this, key, container: null, blobId: null);
            }

            try
            {
                // An unidentifiable version must not outlive its session even
                // when persistence is on: nothing could ever validate it.
                var container = key is not null && Keep ? Persistent() : Session();
                return new PendingContent(
                    this,
                    key,
                    container,
                    blobId: key ?? $"transient-{Guid.NewGuid():n}");
            }
            catch (LiteException exception)
            {
                // To every caller a broken container is what a broken disk is.
                throw new IOException(
                    "The preview cache could not store the file.", exception);
            }
        }
    }

    /// <summary>A put in flight: a destination, then a commit or nothing.</summary>
    public sealed class PendingContent : IDisposable
    {
        private readonly PreviewContentCache _cache;
        private readonly string? _key;
        private readonly LiteDatabase? _container;
        private readonly string? _blobId;
        private readonly MemoryStream? _buffer;
        private Stream? _upload;
        private bool _committed;

        internal PendingContent(
            PreviewContentCache cache,
            string? key,
            LiteDatabase? container,
            string? blobId)
        {
            _cache = cache;
            _key = key;
            _container = container;
            _blobId = blobId;
            _buffer = container is null ? new MemoryStream() : null;
        }

        public Stream Destination => _buffer ?? OpenUpload();

        private Stream OpenUpload()
        {
            if (_upload is not null)
            {
                return _upload;
            }

            try
            {
                var storage = _container!.GetStorage<string>();
                storage.Delete(_blobId!);
                _upload = storage.OpenWrite(_blobId!, _blobId!);
                return _upload;
            }
            catch (LiteException exception)
            {
                throw new IOException(
                    "The preview cache could not store the file.", exception);
            }
        }

        public FilePreviewContent Commit()
        {
            _committed = true;
            if (_buffer is not null)
            {
                var bytes = _buffer.ToArray();
                if (_key is not null)
                {
                    _cache.StoreInMemory(_key, bytes);
                }

                return new MemoryContent(bytes);
            }

            try
            {
                _upload?.Dispose();
                _upload = null;
                var storage = _container!.GetStorage<string>();
                var length = storage.FindById(_blobId!)?.Length ?? 0;
                // The marker is written only after every byte is; its absence
                // is how a torn upload is told apart from a whole one.
                storage.SetMetadata(_blobId!, new BsonDocument
                {
                    ["whole"] = true,
                    ["touched"] = DateTime.UtcNow,
                });
                if (_key is not null)
                {
                    _cache.PruneToBudgetIfPersistent(_container);
                }

                return new ContainerContent(_cache, _container, _blobId!, length);
            }
            catch (LiteException exception)
            {
                throw new IOException(
                    "The preview cache could not store the file.", exception);
            }
        }

        public void Dispose()
        {
            _upload?.Dispose();
            _upload = null;
            _buffer?.Dispose();
            if (_committed || _container is null)
            {
                return;
            }

            try
            {
                // Abandoned: whatever chunks made it in are torn, and torn
                // content is deleted rather than left to be found.
                _container.GetStorage<string>().Delete(_blobId!);
            }
            catch (Exception exception)
                when (exception is LiteException or ObjectDisposedException)
            {
            }
        }
    }

    private void StoreInMemory(string key, byte[] bytes)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_memory.Remove(key, out var replaced))
            {
                _memoryBytes -= replaced.Length;
                _memoryOrder.Remove(key);
            }

            _memory[key] = bytes;
            _memoryOrder.AddFirst(key);
            _memoryBytes += bytes.Length;
            while (_memoryBytes > MemoryBudgetBytes && _memoryOrder.Last is { } oldest)
            {
                _memoryOrder.RemoveLast();
                if (_memory.Remove(oldest.Value, out var evicted))
                {
                    _memoryBytes -= evicted.Length;
                }
            }
        }
    }

    private void PruneToBudgetIfPersistent(LiteDatabase container)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(container, _persistent))
            {
                PruneToBudget(container);
            }
        }
    }

    /// <summary>
    /// A stream over a stored blob, opened under the same gate that guards
    /// pruning so a read never starts against an entry being deleted.
    /// </summary>
    private Stream OpenBlob(LiteDatabase container, string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var storage = container.GetStorage<string>();
                var entry = storage.FindById(key)
                    ?? throw new IOException("The cached preview content is gone.");
                entry.Metadata["touched"] = DateTime.UtcNow;
                storage.SetMetadata(key, entry.Metadata);
                return storage.OpenRead(key);
            }
            catch (Exception exception)
                when (exception is LiteException or ObjectDisposedException)
            {
                throw new IOException(
                    "The cached preview content could not be read.", exception);
            }
        }
    }

    private IEnumerable<LiteDatabase> Containers()
    {
        if (_session is not null)
        {
            yield return _session;
        }

        if (Keep)
        {
            LiteDatabase? persistent = null;
            try
            {
                persistent = Persistent();
            }
            catch (Exception exception)
                when (exception is IOException or LiteException or UnauthorizedAccessException)
            {
                // A store that will not open serves nothing; previews fetch.
            }

            if (persistent is not null)
            {
                yield return persistent;
            }
        }
    }

    private string SessionPath => Path.Combine(_directory, $"session-{_sessionId}.db");

    private string SessionLockPath => Path.Combine(_directory, $"session-{_sessionId}.lock");

    private string PersistentPath => Path.Combine(_directory, PersistentFileName);

    private LiteDatabase Session()
    {
        if (_session is not null)
        {
            return _session;
        }

        // The key never touches disk and derives from nothing: 32 random
        // bytes whose only copy is this string. The session id in the file
        // name is a label for the sweep, not key material.
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessionLock ??= new FileStream(
            SessionLockPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);
        _session = new LiteDatabase(new ConnectionString
        {
            Filename = SessionPath,
            Password = key,
            Connection = ConnectionType.Direct,
        });
        return _session;
    }

    private LiteDatabase Persistent() =>
        _persistent ??= new LiteDatabase(new ConnectionString
        {
            Filename = PersistentPath,
            Connection = ConnectionType.Direct,
        });

    private void PruneToBudget(LiteDatabase container)
    {
        try
        {
            var storage = container.GetStorage<string>();
            var entries = storage.FindAll()
                .OrderByDescending(entry =>
                    entry.Metadata.TryGetValue("touched", out var touched)
                        ? touched.AsDateTime
                        : DateTime.MinValue)
                .ToArray();
            long kept = 0;
            foreach (var entry in entries)
            {
                kept += entry.Length;
                if (kept > Budget)
                {
                    storage.Delete(entry.Id);
                }
            }
        }
        catch (LiteException)
        {
            // A cache that cannot be pruned is a disk-space problem, never a
            // reason to fail the preview that was just stored.
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || Keep)
            {
                return;
            }

            // Turning persistence off is a statement about what may remain on
            // this disk, so what already does remain is removed now, not at
            // some future startup.
            DeletePersistentContainer();
        }
    }

    private void DeletePersistentContainer()
    {
        _persistent?.Dispose();
        _persistent = null;
        TryDelete(PersistentPath);
        TryDelete(LogPathFor(PersistentPath));
    }

    /// <summary>LiteDB keeps its write-ahead log beside the file.</summary>
    private static string LogPathFor(string databasePath) =>
        databasePath[..^".db".Length] + "-log.db";

    private void SweepDeadSessions()
    {
        foreach (var lockPath in Directory.EnumerateFiles(_directory, "session-*.lock"))
        {
            try
            {
                // Taking the lock proves its owner is gone; a live owner holds
                // it exclusively and this open fails.
                using (new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var dbPath = Path.ChangeExtension(lockPath, ".db");
            TryDelete(dbPath);
            TryDelete(LogPathFor(dbPath));
            TryDelete(lockPath);
        }

        // A container with no lock at all is from a run that died before its
        // lock was taken or after it was released; either way nobody owns it.
        foreach (var dbPath in Directory.EnumerateFiles(_directory, "session-*.db"))
        {
            if (!File.Exists(Path.ChangeExtension(dbPath, ".lock")))
            {
                TryDelete(dbPath);
            }
        }
    }

    private static void SweepLegacyPlaintextCache()
    {
        var legacy = Path.Combine(Path.GetTempPath(), LegacyPlaintextDirectoryName);
        try
        {
            if (Directory.Exists(legacy))
            {
                Directory.Delete(legacy, recursive: true);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // Another instance may still be serving from it; its own exit or
            // the next start finishes the job.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_preferences is not null)
            {
                _preferences.Changed -= OnPreferencesChanged;
            }

            _memory.Clear();
            _memoryOrder.Clear();
            _session?.Dispose();
            _persistent?.Dispose();
            _sessionLock?.Dispose();
            TryDelete(SessionPath);
            TryDelete(LogPathFor(SessionPath));
            TryDelete(SessionLockPath);
        }
    }

    private sealed class MemoryContent(byte[] bytes) : FilePreviewContent
    {
        public override long Length => bytes.Length;

        public override Stream OpenRead() => new MemoryStream(bytes, writable: false);

        public override ValueTask<byte[]> ReadAllBytesAsync(
            CancellationToken cancellationToken) => new(bytes);
    }

    private sealed class ContainerContent(
        PreviewContentCache cache,
        LiteDatabase container,
        string key,
        long length) : FilePreviewContent
    {
        public override long Length => length;

        public override Stream OpenRead() => cache.OpenBlob(container, key);
    }

}
