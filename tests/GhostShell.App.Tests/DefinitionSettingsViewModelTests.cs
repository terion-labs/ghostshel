using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DefinitionSettingsViewModelTests
{
    [Fact]
    public void Applying_the_same_catalog_preserves_projected_item_identity()
    {
        var catalog = CreateCatalog(Snapshot());
        using var viewModel = CreateViewModel(catalog);
        var layout = Assert.Single(viewModel.Layouts);
        var profile = Assert.Single(viewModel.KeybindingProfiles);

        viewModel.ApplyCatalog(catalog.Snapshot);

        Assert.Same(layout, Assert.Single(viewModel.Layouts));
        Assert.Same(profile, Assert.Single(viewModel.KeybindingProfiles));
    }

    [Fact]
    public void Replacing_a_keybinding_editor_disposes_the_previous_session()
    {
        var catalog = CreateCatalog(Snapshot());
        using var viewModel = CreateViewModel(catalog);
        Assert.True(viewModel.TrySelectKeybindingProfile(
            Assert.Single(viewModel.KeybindingProfiles),
            out var error));
        Assert.Null(error);
        var previous = Assert.IsType<KeybindingEditorSessionViewModel>(
            viewModel.KeybindingEditorSession);

        Assert.True(viewModel.TryCloneSelectedKeybindingProfile(out error));

        Assert.Null(error);
        Assert.NotSame(previous, viewModel.KeybindingEditorSession);
        Assert.True(IsDisposed(previous));
    }

    [Fact]
    public async Task Clone_save_uses_no_revision_and_reopens_at_the_saved_revision()
    {
        var catalog = CreateCatalog(Snapshot());
        using var viewModel = CreateViewModel(catalog);
        Assert.True(viewModel.TrySelectKeybindingProfile(
            Assert.Single(viewModel.KeybindingProfiles),
            out _));
        Assert.True(viewModel.TryCloneSelectedKeybindingProfile(out _));

        var result = await viewModel.SaveKeybindingEditorAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(catalog.LastExpectedKeymapRevision);
        var saved = Assert.IsType<StoredDefinition<KeymapProfile>>(result.Value);
        Assert.Equal(saved.Revision, viewModel.SelectedKeybindingProfile?.Revision);
        Assert.Equal(saved.Revision, viewModel.KeybindingEditorSession?.Editor.ExpectedRevision);
        Assert.False(viewModel.SelectedKeybindingProfile?.IsUnsaved);
    }

    [Fact]
    public async Task Revision_conflict_keeps_the_layout_draft_open()
    {
        var catalog = CreateCatalog(Snapshot());
        catalog.RejectLayoutSave = true;
        using var viewModel = CreateViewModel(catalog);
        var card = Assert.Single(viewModel.Layouts);
        Assert.True(viewModel.TryBeginEditLayout(card.Id, out var error));
        Assert.Null(error);
        var editor = Assert.IsType<LayoutDesignerViewModel>(viewModel.LayoutDesignerEditor);

        var result = await viewModel.SaveLayoutDesignerAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(card.Revision, catalog.LastExpectedLayoutRevision);
        Assert.Same(editor, viewModel.LayoutDesignerEditor);
    }

    [Fact]
    public void Disposing_the_owner_disposes_the_active_keybinding_session()
    {
        var catalog = CreateCatalog(Snapshot());
        var viewModel = CreateViewModel(catalog);
        viewModel.EnsureKeybindingEditor();
        var session = Assert.IsType<KeybindingEditorSessionViewModel>(
            viewModel.KeybindingEditorSession);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.True(IsDisposed(session));
        Assert.Null(viewModel.KeybindingEditorSession);
        Assert.Null(viewModel.LayoutDesignerEditor);
    }

    private static bool IsDisposed(KeybindingEditorSessionViewModel session) =>
        (bool)typeof(KeybindingEditorSessionViewModel)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

    private static DefinitionCatalogSnapshot Snapshot()
    {
        var layout = new LayoutDefinition(
            new LayoutId("settings.layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Settings layout",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        return DefinitionCatalogSnapshot.Empty with
        {
            Layouts = [Store(layout, 17)],
            Keymaps = [Store(BuiltInKeymaps.TmuxApplication, 23)],
        };
    }

    private static StoredDefinition<T> Store<T>(T definition, long revision)
        where T : IDurableDefinition =>
        new(definition, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static RecordingDefinitionCatalog CreateCatalog(
        DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingDefinitionCatalog>();
        var recording = (RecordingDefinitionCatalog)(object)catalog;
        recording.CurrentSnapshot = snapshot;
        return recording;
    }

    private static DefinitionSettingsViewModel CreateViewModel(
        RecordingDefinitionCatalog catalog) =>
        new((IDefinitionCatalog)(object)catalog);

    public class RecordingDefinitionCatalog : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public bool RejectLayoutSave { get; set; }

        public long? LastExpectedLayoutRevision { get; private set; }

        public long? LastExpectedKeymapRevision { get; private set; }

        public DefinitionCatalogSnapshot Snapshot => CurrentSnapshot;

        public event EventHandler? Changed;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveLayoutAsync) => SaveLayout(
                    (LayoutDefinition)args[0]!,
                    (long?)args[1]),
                nameof(IDefinitionCatalog.SaveKeymapAsync) => SaveKeymap(
                    (KeymapProfile)args[0]!,
                    (long?)args[1]),
                "add_Changed" => AddChanged((EventHandler)args[0]!),
                "remove_Changed" => RemoveChanged((EventHandler)args[0]!),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayout(
            LayoutDefinition definition,
            long? expectedRevision)
        {
            LastExpectedLayoutRevision = expectedRevision;
            if (RejectLayoutSave)
            {
                return ValueTask.FromResult(
                    DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(new(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "The layout changed before it could be saved.",
                        (expectedRevision ?? 0) + 1)));
            }

            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1)));
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymap(
            KeymapProfile definition,
            long? expectedRevision)
        {
            LastExpectedKeymapRevision = expectedRevision;
            var stored = Store(definition, (expectedRevision ?? 0) + 1);
            CurrentSnapshot = CurrentSnapshot with
            {
                Keymaps =
                [
                    .. CurrentSnapshot.Keymaps.Where(item => item.Value.Id != definition.Id),
                    stored,
                ],
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<KeymapProfile>>.Success(stored));
        }

        private object? AddChanged(EventHandler handler)
        {
            Changed += handler;
            return null;
        }

        private object? RemoveChanged(EventHandler handler)
        {
            Changed -= handler;
            return null;
        }
    }
}
