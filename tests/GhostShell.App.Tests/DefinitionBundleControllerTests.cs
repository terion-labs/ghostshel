using System.Collections.Concurrent;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DefinitionBundleControllerTests
{
    [Fact]
    public async Task Export_safety_preflights_then_atomically_writes_the_selected_file()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("definitions.json");
        await File.WriteAllTextAsync(path, "previous contents");
        var bundle = Bundle(Document("layout-one"));
        var store = new RecordingBundleStore { ExportedBundle = bundle };
        var picker = new RecordingPathPicker { ExportPath = path };
        var controller = CreateController(store, picker);

        var result = await controller.ExportAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Path.GetFullPath(path), result.Value!.Path);
        Assert.Equal(1, result.Value.DefinitionCount);
        Assert.Equal(bundle.ExportedAt, result.Value.ExportedAt);
        Assert.Equal(DefinitionBundleController.SuggestedExportFileName, picker.SuggestedFileName);
        var safety = Assert.Single(store.PreflightCalls);
        Assert.Same(bundle, safety.Bundle);
        Assert.Equal(DefinitionImportMode.ReplaceExisting, safety.Mode);
        var written = await ReadBundleAsync(path);
        Assert.Equal("layout-one", Assert.Single(written.Definitions).Id);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task Unsafe_export_never_reaches_the_user_file()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("definitions.json");
        const string original = "existing-safe-file";
        await File.WriteAllTextAsync(path, original);
        const string secret = "vault-secret-canary";
        var unsafeBundle = Bundle(Document(
            "unsafe-layout",
            $$"""{"id":"unsafe-layout","password":"{{secret}}"}"""));
        var store = new RecordingBundleStore
        {
            ExportedBundle = unsafeBundle,
            PreflightFactory = (bundle, mode) => SuccessfulPreflight(
                bundle,
                mode,
                new DefinitionImportIssue(
                    DefinitionImportIssueCode.UnsafePayload,
                    new DefinitionKey(DefinitionKind.Layout, "unsafe-layout"),
                    "The definition contains a secret value.",
                    IsBlocking: true)),
        };
        var controller = CreateController(
            store,
            new RecordingPathPicker { ExportPath = path });

        var result = await controller.ExportAsync(CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.UnsafePayload, result.Error!.Code);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task Dismissing_export_picker_is_a_typed_cancellation_without_store_access()
    {
        var store = new RecordingBundleStore();
        var controller = CreateController(store, new RecordingPathPicker());

        var result = await controller.ExportAsync(CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, store.ExportCalls);
        Assert.Empty(store.PreflightCalls);
    }

    [Fact]
    public async Task Import_preflight_surfaces_structured_issues_and_conflicts_without_payloads()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("definitions.json");
        await WriteBundleAsync(path, Bundle(Document("layout-conflict")));
        var conflict = new DefinitionImportIssue(
            DefinitionImportIssueCode.ExistingIdentity,
            new DefinitionKey(DefinitionKind.Layout, "layout-conflict"),
            "A layout already exists.",
            IsBlocking: true);
        var dependency = new DefinitionImportIssue(
            DefinitionImportIssueCode.MissingDependency,
            new DefinitionKey(DefinitionKind.Screen, "screen-one"),
            "The screen needs another layout.",
            IsBlocking: true);
        var store = new RecordingBundleStore
        {
            PreflightFactory = (bundle, mode) => SuccessfulPreflight(
                bundle,
                mode,
                conflict,
                dependency),
        };
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path });

        var result = await controller.PreflightImportAsync(
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var plan = result.Value!;
        Assert.Equal(Path.GetFullPath(path), plan.Path);
        Assert.Equal(DefinitionImportMode.FailOnConflict, plan.Mode);
        Assert.Equal(1, plan.DefinitionCount);
        Assert.Equal([conflict, dependency], plan.Issues);
        Assert.Equal(conflict, Assert.Single(plan.Conflicts));
        Assert.False(plan.CanApply);
        Assert.DoesNotContain(
            plan.GetType().GetProperties(),
            property => property.PropertyType == typeof(PortableDefinitionBundle)
                || property.Name.Contains("Payload", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("null")]
    [InlineData("{\"formatVersion\":1,\"exportedAt\":\"2026-07-22T12:00:00Z\",\"definitions\":[],\"unknown\":true}")]
    public async Task Corrupt_or_noncanonical_import_never_reaches_store_preflight(string json)
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("invalid.json");
        await File.WriteAllTextAsync(path, json);
        var store = new RecordingBundleStore();
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path });

        var result = await controller.PreflightImportAsync(
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error!.Code);
        Assert.Empty(store.PreflightCalls);
    }

    [Fact]
    public async Task Oversized_import_is_rejected_before_allocating_or_parsing_it()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("oversized.json");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(DefinitionBundleController.MaximumImportBytes + 1);
        }

        var store = new RecordingBundleStore();
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path });

        var result = await controller.PreflightImportAsync(
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error!.Code);
        Assert.Empty(store.PreflightCalls);
    }

    [Fact]
    public async Task Blocking_preflight_cannot_be_applied_even_if_a_caller_ignores_the_ui_state()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("conflict.json");
        await WriteBundleAsync(path, Bundle(Document("layout-conflict")));
        var store = new RecordingBundleStore
        {
            PreflightFactory = (bundle, mode) => SuccessfulPreflight(
                bundle,
                mode,
                new DefinitionImportIssue(
                    DefinitionImportIssueCode.ExistingIdentity,
                    new DefinitionKey(DefinitionKind.Layout, "layout-conflict"),
                    "A layout already exists.",
                    IsBlocking: true)),
        };
        var refresh = new RecordingImportRefresh();
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path },
            refresh);
        var plan = (await controller.PreflightImportAsync(
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None)).Value!;

        var applied = await controller.ConfirmAndApplyImportAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, applied.Error!.Code);
        Assert.Empty(store.Committed);
        Assert.Equal(0, refresh.Calls);
    }

    [Fact]
    public async Task Confirm_applies_the_exact_preflighted_payload_then_refreshes_catalog()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("import.json");
        await WriteBundleAsync(path, Bundle(Document("layout-reviewed")));
        var store = new RecordingBundleStore
        {
            CommitResult = DefinitionStoreResult<DefinitionImportResult>.Success(new(2, 1)),
        };
        var refresh = new RecordingImportRefresh();
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path },
            refresh);
        var plan = (await controller.PreflightImportAsync(
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None)).Value!;
        await WriteBundleAsync(path, Bundle(Document("layout-substituted")));

        var applied = await controller.ConfirmAndApplyImportAsync(
            plan,
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error?.Message);
        Assert.Equal(2, applied.Value!.Inserted);
        Assert.Equal(1, applied.Value.Replaced);
        Assert.True(applied.Value.CatalogReloaded);
        Assert.Null(applied.Value.ReloadError);
        Assert.Equal(1, refresh.Calls);
        var committed = Assert.Single(store.Committed);
        Assert.Same(plan.Preflight, committed);
        Assert.Equal("layout-reviewed", Assert.Single(committed.Bundle.Definitions).Id);
    }

    [Fact]
    public async Task Commit_failure_is_returned_unchanged_and_does_not_refresh()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("import.json");
        await WriteBundleAsync(path, Bundle(Document("layout-one")));
        var error = new DefinitionStoreError(
            DefinitionStoreErrorCode.DependencyConflict,
            "The catalog changed after preflight.");
        var store = new RecordingBundleStore
        {
            CommitResult = DefinitionStoreResult<DefinitionImportResult>.Failure(error),
        };
        var refresh = new RecordingImportRefresh();
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path },
            refresh);
        var plan = (await controller.PreflightImportAsync(
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None)).Value!;

        var applied = await controller.ConfirmAndApplyImportAsync(
            plan,
            CancellationToken.None);

        Assert.Same(error, applied.Error);
        Assert.Equal(0, refresh.Calls);
    }

    [Fact]
    public async Task Refresh_failure_does_not_misreport_a_durable_import_as_rolled_back()
    {
        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("import.json");
        await WriteBundleAsync(path, Bundle(Document("layout-one")));
        var reloadError = new DefinitionStoreError(
            DefinitionStoreErrorCode.StorageUnavailable,
            "The UI catalog could not reload.");
        var refresh = new RecordingImportRefresh
        {
            Result = DefinitionStoreResult<Unit>.Failure(reloadError),
        };
        var store = new RecordingBundleStore
        {
            CommitResult = DefinitionStoreResult<DefinitionImportResult>.Success(new(1, 0)),
        };
        var controller = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path },
            refresh);
        var plan = (await controller.PreflightImportAsync(
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None)).Value!;

        var applied = await controller.ConfirmAndApplyImportAsync(
            plan,
            CancellationToken.None);

        Assert.True(applied.IsSuccess);
        Assert.Equal(1, applied.Value!.Inserted);
        Assert.False(applied.Value.CatalogReloaded);
        Assert.Same(reloadError, applied.Value.ReloadError);
    }

    [Fact]
    public async Task Picker_cancellation_and_store_cancellation_are_typed_and_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var throwingPicker = new RecordingPathPicker { ThrowImportCancellation = true };
        var pickerController = CreateController(new RecordingBundleStore(), throwingPicker);

        var pickerCancelled = await pickerController.PreflightImportAsync(
            DefinitionImportMode.FailOnConflict,
            cancellation.Token);

        Assert.Equal(DefinitionStoreErrorCode.Cancelled, pickerCancelled.Error!.Code);

        using var temporary = TemporaryDirectory.Create();
        var path = temporary.PathFor("import.json");
        await WriteBundleAsync(path, Bundle(Document("layout-one")));
        var storeError = new DefinitionStoreError(
            DefinitionStoreErrorCode.Cancelled,
            "Preflight was cancelled.");
        var store = new RecordingBundleStore
        {
            PreflightFactory = (_, _) =>
                DefinitionStoreResult<DefinitionImportPreflight>.Failure(storeError),
        };
        var storeController = CreateController(
            store,
            new RecordingPathPicker { ImportPath = path });

        var storeCancelled = await storeController.PreflightImportAsync(
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);

        Assert.Same(storeError, storeCancelled.Error);
    }

    private static DefinitionBundleController CreateController(
        RecordingBundleStore store,
        RecordingPathPicker picker,
        RecordingImportRefresh? refresh = null) =>
        new(store, picker, refresh ?? new RecordingImportRefresh());

    private static PortableDefinitionBundle Bundle(
        params PortableDefinitionDocument[] documents) =>
        new(
            PortableDefinitionBundle.CurrentFormatVersion,
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero),
            documents);

    private static PortableDefinitionDocument Document(
        string id,
        string? payloadJson = null) =>
        new(
            DefinitionKind.Layout,
            id,
            1,
            $"Layout {id}",
            payloadJson
                ?? $"{{\"id\":{{\"value\":\"{id}\"}},\"schemaVersion\":1,"
                + $"\"name\":\"Layout {id}\",\"root\":{{\"slotId\":{{\"value\":\"main\"}},"
                + "\"kind\":0,\"weight\":1}}}");

    private static DefinitionStoreResult<DefinitionImportPreflight> SuccessfulPreflight(
        PortableDefinitionBundle bundle,
        DefinitionImportMode mode,
        params DefinitionImportIssue[] issues) =>
        DefinitionStoreResult<DefinitionImportPreflight>.Success(
            new DefinitionImportPreflight(bundle, mode, issues));

    private static async Task WriteBundleAsync(
        string path,
        PortableDefinitionBundle bundle)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            bundle,
            DefinitionBundleJsonContext.Default.PortableDefinitionBundle);
    }

    private static async Task<PortableDefinitionBundle> ReadBundleAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync(
            stream,
            DefinitionBundleJsonContext.Default.PortableDefinitionBundle))!;
    }

    private sealed class RecordingBundleStore : IDefinitionBundleStore
    {
        private int _exportCalls;

        public PortableDefinitionBundle ExportedBundle { get; set; } = Bundle();

        public Func<
            PortableDefinitionBundle,
            DefinitionImportMode,
            DefinitionStoreResult<DefinitionImportPreflight>>
            PreflightFactory
        { get; set; } =
                (bundle, mode) => SuccessfulPreflight(bundle, mode);

        public DefinitionStoreResult<DefinitionImportResult> CommitResult { get; set; } =
            DefinitionStoreResult<DefinitionImportResult>.Success(new(0, 0));

        public int ExportCalls => Volatile.Read(ref _exportCalls);

        public ConcurrentQueue<(PortableDefinitionBundle Bundle, DefinitionImportMode Mode)>
            PreflightCalls
        { get; } = [];

        public ConcurrentQueue<DefinitionImportPreflight> Committed { get; } = [];

        public ValueTask<DefinitionStoreResult<PortableDefinitionBundle>> ExportAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _exportCalls);
            return ValueTask.FromResult(
                DefinitionStoreResult<PortableDefinitionBundle>.Success(ExportedBundle));
        }

        public ValueTask<DefinitionStoreResult<DefinitionImportPreflight>> PreflightImportAsync(
            PortableDefinitionBundle bundle,
            DefinitionImportMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreflightCalls.Enqueue((bundle, mode));
            return ValueTask.FromResult(PreflightFactory(bundle, mode));
        }

        public ValueTask<DefinitionStoreResult<DefinitionImportResult>> CommitImportAsync(
            DefinitionImportPreflight preflight,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed.Enqueue(preflight);
            return ValueTask.FromResult(CommitResult);
        }
    }

    private sealed class RecordingPathPicker : IDefinitionBundlePathPicker
    {
        public string? ExportPath { get; init; }

        public string? ImportPath { get; init; }

        public bool ThrowImportCancellation { get; init; }

        public string? SuggestedFileName { get; private set; }

        public ValueTask<string?> PickExportPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuggestedFileName = suggestedFileName;
            return ValueTask.FromResult(ExportPath);
        }

        public ValueTask<string?> PickImportPathAsync(CancellationToken cancellationToken)
        {
            if (ThrowImportCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ImportPath);
        }
    }

    private sealed class RecordingImportRefresh : IDefinitionBundleImportRefresh
    {
        private int _calls;

        public DefinitionStoreResult<Unit> Result { get; init; } =
            DefinitionStoreResult<Unit>.Success(Unit.Value);

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<DefinitionStoreResult<Unit>> ReloadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ghostshell-bundle-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
