using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class WorkspaceAutoSaveCoordinatorTests
{
    private static readonly WorkspaceId WorkspaceId = new("autosave-workspace");

    [Fact]
    public async Task Flush_writes_the_captured_workspace_without_waiting_for_the_debounce()
    {
        var (catalog, recorder, stored) = CreateCatalog();
        var runtime = CreateLauncherWorkspace();
        using var coordinator = new WorkspaceAutoSaveCoordinator(
            catalog,
            () => runtime,
            () => new RuntimeHistorySource(stored.Value.Key, stored.Value.Name),
            () => false);

        coordinator.Queue();
        await coordinator.FlushAsync();

        var saved = Assert.IsType<WorkspaceDefinition>(recorder.SavedWorkspace);
        Assert.Equal(WorkspaceId, saved.Id);
        Assert.True(saved.AutoSave);
        Assert.True(saved.IsIsolated);
        Assert.Equal(stored.Value.IsolationMounts, saved.IsolationMounts);
        Assert.Empty(saved.Entries);
        Assert.Equal(stored.Revision, recorder.ExpectedRevision);
    }

    [Fact]
    public async Task Seal_rejects_new_queue_and_flush_requests()
    {
        var (catalog, recorder, stored) = CreateCatalog();
        using var coordinator = new WorkspaceAutoSaveCoordinator(
            catalog,
            CreateLauncherWorkspace,
            () => new RuntimeHistorySource(stored.Value.Key, stored.Value.Name),
            () => false);

        coordinator.Seal();
        coordinator.Queue();
        await coordinator.FlushAsync();

        Assert.Null(recorder.SavedWorkspace);
    }

    private static RuntimeWorkspaceViewModel CreateLauncherWorkspace()
    {
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Autosave",
            "#123456",
            []);
        var launcher = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "New tab",
            "Launcher");
        launcher.AddPlaceholder(PanelSide.Right);
        runtime.Tabs.Add(launcher);
        runtime.ActiveTab = launcher;
        return runtime;
    }

    private static (
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Recorder,
        StoredDefinition<WorkspaceDefinition> Stored) CreateCatalog()
    {
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Autosave",
            "",
            "#123456",
            [new WorkspaceEntry.ConnectionReference(
                WorkspaceEntryId.New(),
                new ConnectionId("local"))],
            autoSave: true,
            isIsolated: true,
            isolationMounts:
            [
                new(
                    Path.Combine(Path.GetTempPath(), "ghostshell-autosave"),
                    "/workspace",
                    IsReadOnly: false),
            ]);
        var stored = new StoredDefinition<WorkspaceDefinition>(
            workspace,
            7,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var recorder = (RecordingCatalogProxy)(object)catalog;
        recorder.Snapshot = DefinitionCatalogSnapshot.Empty with { Workspaces = [stored] };
        return (catalog, recorder, stored);
    }

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public WorkspaceDefinition? SavedWorkspace { get; private set; }

        public long? ExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                "SaveWorkspaceWithLayoutsAsync" => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Save(object?[] args)
        {
            SavedWorkspace = (WorkspaceDefinition)args[0]!;
            ExpectedRevision = (long?)args[1];
            return ValueTask.FromResult<DefinitionStoreError?>(null);
        }
    }
}
