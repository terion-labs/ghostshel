using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

/// <summary>
/// The drain error is a promise that failed session-metadata persistence is
/// said out loud at exit — but a failure a successful retry has since
/// recovered is a ghost, and reporting ghosts at quit taught the user to
/// ignore the message. Observed live: with keys sealed under the startup
/// PIN, the first history read runs against a database that cannot open
/// yet; the post-unlock retry succeeds, and quit still printed the corpse
/// of the pre-unlock failure.
/// </summary>
public sealed class RecentSessionDrainErrorTests
{
    [Fact]
    public async Task A_recovered_history_failure_is_not_reported_at_exit()
    {
        var store = new FlakyStore();
        var history = new RecentSessionHistory(store, TimeProvider.System);
        using var viewModel = CreateViewModel(history);

        // The construction-time load ran against the failing store.
        var beforeRetry = await viewModel.FlushRecentSessionHistoryAsync(
            CancellationToken.None);
        Assert.False(beforeRetry.IsSuccess);

        // The store heals — the unlock happened — and the retry succeeds.
        store.Healed = true;
        Assert.True(await viewModel.RetryRecentSessionHistoryAsync(CancellationToken.None));

        var afterRetry = await viewModel.FlushRecentSessionHistoryAsync(
            CancellationToken.None);
        Assert.True(
            afterRetry.IsSuccess,
            $"A recovered failure was still reported at exit: {afterRetry.Error?.Message}");
    }

    [Fact]
    public async Task An_unrecovered_history_failure_is_still_reported_at_exit()
    {
        var store = new FlakyStore();
        var history = new RecentSessionHistory(store, TimeProvider.System);
        using var viewModel = CreateViewModel(history);

        // A failed retry clears nothing: the promise stands.
        _ = await viewModel.RetryRecentSessionHistoryAsync(CancellationToken.None);
        var flush = await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.False(flush.IsSuccess);
    }

    private static MainWindowViewModel CreateViewModel(RecentSessionHistory history)
    {
        var files = new EmptyFileClients();
        return new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingProxy>(),
            DispatchProxy.Create<IDefinitionCatalog, EmptyCatalogProxy>(),
            DispatchProxy.Create<IConnectionRuntime, RejectingProxy2>(),
            DispatchProxy.Create<ISecretVault, EmptyVaultProxy>(),
            files,
            files,
            new TerminalStartupCommandDispatcher(
                DispatchProxy.Create<IAuditStore, SucceedingAuditProxy>(),
                TimeProvider.System),
            recentSessionHistory: history);
    }

    /// <summary>
    /// Retention and listing refuse until healed — the shape of a database
    /// whose key has not arrived yet.
    /// </summary>
    private sealed class FlakyStore : IRecentSessionStore, IRecentSessionRetentionStore
    {
        public bool Healed { get; set; }

        private RecentSessionStoreResult<T> Refuse<T>() =>
            RecentSessionStoreResult<T>.Failure(new RecentSessionStoreError(
                RecentSessionStoreErrorCode.StorageFailure,
                "The recent-session store could not read retention."));

        public ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
            GetRetentionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Healed
                ? RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>.Success(
                    new StoredRecentSessionRetentionPolicy(
                        RecentSessionRetentionPolicy.Default,
                        revision: 1))
                : Refuse<StoredRecentSessionRetentionPolicy>());

        public ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
            UpdateRetentionAsync(
                RecentSessionRetentionPolicy policy,
                long expectedRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
            ListRecentAsync(RecentSessionQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Healed
                ? RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success([])
                : Refuse<IReadOnlyList<RecentSessionRecord>>());

        public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
    }

    public class RejectingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    public class RejectingProxy2 : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    public class EmptyCatalogProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => DefinitionCatalogSnapshot.Empty,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }

    public class EmptyVaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.ListMetadata,
                    "test",
                    "test_vault",
                    "Test vault"),
                "ListMetadataAsync" => ValueTask.FromResult(
                    SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([])),
                "Dispose" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }

    public class SucceedingAuditProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class EmptyFileClients : IFilePanelClient, IFileTransferQueueClient
    {
        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; } = [];

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; } = [];

        public event EventHandler? TransfersChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
