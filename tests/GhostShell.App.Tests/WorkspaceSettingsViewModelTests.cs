using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class WorkspaceSettingsViewModelTests
{
    [Fact]
    public void Begin_edit_projects_the_catalog_and_publishes_revision_identity()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);

        var opened = viewModel.TryBeginEdit(
            WorkspaceId,
            out var identity,
            out var error);

        Assert.True(opened);
        Assert.Null(error);
        Assert.Equal(WorkspaceRevision, identity?.Revision);
        Assert.Equal("Operations", identity?.Name);
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        Assert.Equal(WorkspaceRevision, editor.ExpectedRevision);
        Assert.Contains(editor.ConnectionOptions, option => option.Id == ConnectionId);
        Assert.Contains(editor.LayoutOptions, option => option.Id == LayoutId);
        Assert.Contains(editor.ScreenOptions, option => option.Id == ScreenId);
        Assert.True(viewModel.HasEditor);
    }

    [Fact]
    public void Dirty_draft_blocks_replacement_and_preserves_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        editor.Name = "Unsaved draft";

        var opened = viewModel.TryBeginCreate(out var identity, out var error);

        Assert.False(opened);
        Assert.Null(identity);
        Assert.Contains("discard", error, StringComparison.OrdinalIgnoreCase);
        Assert.Same(editor, viewModel.Editor);
        Assert.Equal("Unsaved draft", editor.Name);
    }

    [Fact]
    public async Task Successful_save_forwards_revision_and_dismisses_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        viewModel.Editor!.Name = "Production";

        var result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceRevision, fixture.Proxy.LastExpectedRevision);
        Assert.Equal("Production", fixture.Proxy.LastSavedWorkspace?.Name);
        Assert.Null(viewModel.Editor);
        Assert.False(viewModel.HasEditor);
    }

    [Fact]
    public async Task Revision_conflict_preserves_the_live_draft()
    {
        var fixture = CreateCatalog(Snapshot());
        fixture.Proxy.RejectSave = true;
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);
        editor.Name = "Conflicting draft";

        var result = await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(WorkspaceRevision, fixture.Proxy.LastExpectedRevision);
        Assert.Same(editor, viewModel.Editor);
        Assert.Equal("Conflicting draft", editor.Name);
    }

    [Fact]
    public async Task Stale_request_is_rejected_before_catalog_persistence()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var current = viewModel.Editor!.CreateSaveRequest();
        var stale = current with { ExpectedRevision = current.ExpectedRevision + 1 };

        var result = await viewModel.SaveAsync(stale, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error?.Code);
        Assert.Null(fixture.Proxy.LastSavedWorkspace);
        Assert.NotNull(viewModel.Editor);
    }

    [Fact]
    public void Disposing_the_owner_disposes_and_releases_the_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        var viewModel = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(viewModel.TryBeginEdit(WorkspaceId, out _, out _));
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.Editor);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Null(viewModel.Editor);
        Assert.True(IsDisposed(editor));
    }

    private const long WorkspaceRevision = 17;
    private static readonly WorkspaceId WorkspaceId = new("workspace.settings-owner");
    private static readonly ConnectionId ConnectionId = new("connection.settings-owner");
    private static readonly LayoutId LayoutId = new("layout.settings-owner");
    private static readonly ScreenId ScreenId = new("screen.settings-owner");

    private static bool IsDisposed(WorkspaceEditorViewModel editor) =>
        (bool)typeof(WorkspaceEditorViewModel)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

    private static DefinitionCatalogSnapshot Snapshot()
    {
        var connection = new ConnectionProfile(
            ConnectionId,
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var layout = new LayoutDefinition(
            LayoutId,
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            ScreenId,
            ScreenDefinition.CurrentSchemaVersion,
            "Shell",
            null,
            LayoutId,
            []);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            "Production workspace",
            ThemePreference.BronzeFallback.ToString(),
            []);
        return DefinitionCatalogSnapshot.Empty with
        {
            Connections = [Store(connection, 3)],
            Layouts = [Store(layout, 5)],
            Screens = [Store(screen, 7)],
            Workspaces = [Store(workspace, WorkspaceRevision)],
        };
    }

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogFixture CreateCatalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = snapshot;
        return new(catalog, proxy);
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public bool RejectSave { get; set; }

        public WorkspaceDefinition? LastSavedWorkspace { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveWorkspaceAsync) => SaveWorkspace(
                    (WorkspaceDefinition)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
            SaveWorkspace(
                WorkspaceDefinition definition,
                long? expectedRevision)
        {
            LastSavedWorkspace = definition;
            LastExpectedRevision = expectedRevision;
            if (RejectSave)
            {
                return ValueTask.FromResult(
                    DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Failure(new(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "The workspace changed before it could be saved.",
                        (expectedRevision ?? 0) + 1)));
            }

            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1)));
        }
    }
}
