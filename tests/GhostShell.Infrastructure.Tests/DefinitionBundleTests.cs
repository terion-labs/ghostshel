using System.Collections;
using System.Text.Json.Nodes;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class DefinitionBundleTests
{
    [Fact]
    public async Task ExportContainsAValidatedDurableDefinition()
    {
        await using var temporary = TemporaryDatabase.Create();
        var definition = DurableDefinitionFixtures.Layout();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None)).IsSuccess);
        var bundles = CreateBundleStore(temporary);

        var exported = await bundles.ExportAsync(CancellationToken.None);

        Assert.True(exported.IsSuccess, exported.Error?.Message);
        var document = Assert.Single(exported.Value!.Definitions);
        Assert.Equal(DefinitionKind.Layout, document.Kind);
        Assert.Equal(definition.Key.Value, document.Id);
        Assert.Contains(definition.Key.Value, document.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpProfileImportsExportsAndKeepsVaultReferencesOutOfTheDefinitionGraph()
    {
        await using var temporary = TemporaryDatabase.Create();
        var reference = new SecretRef("vault-mcp-token");
        var profile = DurableDefinitionFixtures.McpServer(
            "mcp-production",
            "Production MCP",
            reference);
        var document = DurableDefinitionFixtures.Document(profile);
        var bundles = CreateBundleStore(temporary);

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);
        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var safetyNotice = Assert.Single(preflight.Value.Issues);
        Assert.Equal(
            DefinitionImportIssueCode.ImportedMcpProfileDisabled,
            safetyNotice.Code);
        Assert.False(safetyNotice.IsBlocking);

        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None);
        var exported = await bundles.ExportAsync(CancellationToken.None);
        var repository = new SqliteDefinitionRepository<McpServerProfile>(
            temporary.Database,
            TimeProvider.System);
        var loaded = await repository.GetAsync(profile.Key, CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(1, committed.Value!.Inserted);
        Assert.True(exported.IsSuccess, exported.Error?.Message);
        var exportedProfile = Assert.Single(exported.Value!.Definitions);
        Assert.NotEqual(document.PayloadJson, exportedProfile.PayloadJson);
        Assert.Contains(
            "\"isEnabled\":false",
            exportedProfile.PayloadJson,
            StringComparison.Ordinal);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(reference, Assert.Single(loaded.Value!.Value.Environment).Reference);
        Assert.False(loaded.Value.Value.IsEnabled);
        Assert.Empty(DefinitionReferenceExtractor.Extract(loaded.Value.Value));
    }

    [Fact]
    public async Task ImportRejectsSecretValueFieldsBeforeWritingAnything()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var definition = DurableDefinitionFixtures.Layout();
        var document = DurableDefinitionFixtures.Document(definition);
        document = document with
        {
            PayloadJson = InsertJsonProperty(
                document.PayloadJson,
                "\"password\":\"sentinel-secret\""),
        };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.UnsafePayload);
        var exported = await bundles.ExportAsync(CancellationToken.None);
        Assert.Empty(exported.Value!.Definitions);
    }

    [Fact]
    public async Task ImportRejectsEveryUnmappedFieldNotJustKnownSecretNames()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var document = DurableDefinitionFixtures.Document(
            DurableDefinitionFixtures.Layout());
        document = document with
        {
            PayloadJson = InsertJsonProperty(
                document.PayloadJson,
                "\"credentialBlob\":\"sentinel-secret\""),
        };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload);
    }

    [Fact]
    public async Task UnknownDefinitionKindIsRejectedByPreflightAndCommit()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var bundle = Bundle(new PortableDefinitionDocument(
            new DefinitionKind("future-kind"),
            "future-one",
            1,
            "Future",
            "{\"id\":{\"value\":\"future-one\"},\"schemaVersion\":1,\"name\":\"Future\"}"));
        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.UnsupportedKind);

        var forged = new DefinitionImportPreflight(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            Issues: []);
        var committed = await bundles.CommitImportAsync(forged, CancellationToken.None);
        Assert.Equal(DefinitionStoreErrorCode.UnsupportedKind, committed.Error!.Code);
    }

    [Fact]
    public async Task ImportRejectsPayloadIdentityThatDiffersFromEnvelope()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var document = DurableDefinitionFixtures.Document(
            DurableDefinitionFixtures.Layout());
        document = document with { Name = "Different envelope name" };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload);
    }

    [Fact]
    public async Task ImportRejectsFutureDefinitionSchema()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var document = DurableDefinitionFixtures.Document(
            DurableDefinitionFixtures.Layout()) with
        {
            SchemaVersion = LayoutDefinition.CurrentSchemaVersion + 1,
        };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.UnsupportedSchema);
    }

    [Fact]
    public async Task ImportRequiresComputedSchemaPropertyToMatchEnvelope()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var document = DurableDefinitionFixtures.Document(ThemePreference.Default);
        document = document with
        {
            // Derived from the definition rather than pinned, so raising a
            // schema version does not silently turn this into a no-op edit.
            PayloadJson = document.PayloadJson.Replace(
                $"\"schemaVersion\":{ThemePreference.CurrentSchemaVersion}",
                "\"schemaVersion\":999",
                StringComparison.Ordinal),
        };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload);
    }

    [Fact]
    public async Task ImportAcceptsSchemaOneThemeWithoutTextScaleOverride()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var document = DurableDefinitionFixtures.Document(ThemePreference.Default);
        var legacyPayload = document.PayloadJson.Replace(
            ",\"textScaleOverride\":null",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(document.PayloadJson, legacyPayload);

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document with { PayloadJson = legacyPayload }),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
    }

    [Theory]
    [InlineData("commandId")]
    [InlineData("prefixStroke")]
    public async Task ImportRequiresAllNonOptionalConstructorValues(string missingValue)
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var keymap = new KeymapProfile(
            new KeymapProfileId("required-values"),
            "Required values",
            KeymapLayer.Application,
            [
                new CommandBinding(
                    new CommandId("app.test"),
                    KeySequence.Of(new KeyStroke("K", KeyModifiers.Control)),
                    CommandContext.Global),
            ],
            new PrefixConfiguration(
                new KeyStroke("B", KeyModifiers.Control),
                TimeSpan.FromSeconds(1),
                repeatable: false,
                FailedSequenceBehavior.DiscardAndShowHint));
        var document = DurableDefinitionFixtures.Document(keymap);
        var payload = JsonNode.Parse(document.PayloadJson)!.AsObject();
        if (missingValue == "commandId")
        {
            var binding = payload["bindings"]!.AsArray()[0]!.AsObject();
            Assert.True(
                binding.Remove("commandId"),
                $"Binding properties: {string.Join(", ", binding.Select(item => item.Key))}");
        }
        else
        {
            var prefix = payload["prefix"]!.AsObject();
            Assert.True(
                prefix.Remove("stroke"),
                $"Prefix properties: {string.Join(", ", prefix.Select(item => item.Key))}");
        }

        document = document with { PayloadJson = payload.ToJsonString() };
        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload);
    }

    [Fact]
    public async Task ImportRejectsNullInNonNullableAgentPolicyFields()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var screen = new ScreenDefinition(
            new ScreenId("policy-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Policy screen",
            description: null,
            new LayoutId("layout-one"),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-one"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ],
            agentPolicyOverride: AgentPolicy.Default);
        var document = DurableDefinitionFixtures.Document(screen);
        var payload = JsonNode.Parse(document.PayloadJson)!.AsObject();
        payload["agentPolicyOverride"]!.AsObject()["provider"] = null;
        document = document with { PayloadJson = payload.ToJsonString() };

        var preflight = await bundles.PreflightImportAsync(
            Bundle(document),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload);
    }

    [Fact]
    public async Task ImportRejectsRunLocalYoloInDurablePolicyOverrides()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var screen = new ScreenDefinition(
            new ScreenId("yolo-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "YOLO screen",
            description: null,
            new LayoutId("missing-layout"),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ],
            agentPolicyOverride: yoloPolicy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("yolo-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "YOLO workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: yoloPolicy);
        var bundle = Bundle(
            DurableDefinitionFixtures.Document(screen),
            DurableDefinitionFixtures.Document(workspace));

        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload
                && issue.Definition == screen.Key
                && issue.Message.Contains("YOLO", StringComparison.Ordinal));
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload
                && issue.Definition == workspace.Key
                && issue.Message.Contains("YOLO", StringComparison.Ordinal));

        var forged = new DefinitionImportPreflight(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            Issues: []);
        var committed = await bundles.CommitImportAsync(forged, CancellationToken.None);
        var exported = await bundles.ExportAsync(CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, committed.Error!.Code);
        Assert.Empty(exported.Value!.Definitions);
    }

    [Fact]
    public async Task LayoutAndDependentScreenCanBeImportedAsOneValidatedBatch()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var layout = DurableDefinitionFixtures.Layout();
        var screen = DurableDefinitionFixtures.Screen();
        var bundle = Bundle(
            DurableDefinitionFixtures.Document(layout),
            DurableDefinitionFixtures.Document(screen));
        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(2, committed.Value!.Inserted);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await screens.GetAsync(screen.Key, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ImportRejectsDuplicateAiProviderFallbackOrdersWithoutWritingAnything()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var primary = DurableDefinitionFixtures.AiProvider(
            "ai-primary",
            "Primary",
            order: 0);
        var duplicate = DurableDefinitionFixtures.AiProvider(
            "ai-duplicate",
            "Duplicate",
            order: 0);
        var bundle = Bundle(
            DurableDefinitionFixtures.Document(primary),
            DurableDefinitionFixtures.Document(duplicate));

        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.InvalidPayload
                && issue.Message.Contains("fallback order 0", StringComparison.Ordinal));

        var forged = new DefinitionImportPreflight(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            Issues: []);
        var committed = await bundles.CommitImportAsync(forged, CancellationToken.None);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, committed.Error!.Code);
        var providers = new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System);
        Assert.Empty((await providers.ListAsync(CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task ImportAcceptsDistinctAiProviderFallbackOrders()
    {
        await using var temporary = TemporaryDatabase.Create();
        var providers = new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System);
        var storedPrimary = DurableDefinitionFixtures.AiProvider(
            "ai-primary",
            "Primary",
            order: 0);
        var storedFallback = DurableDefinitionFixtures.AiProvider(
            "ai-fallback",
            "Fallback",
            order: 1);
        Assert.True((await providers.SaveAsync(
            storedPrimary,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await providers.SaveAsync(
            storedFallback,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var bundles = CreateBundleStore(temporary);
        var primary = DurableDefinitionFixtures.AiProvider(
            "ai-primary",
            "Primary",
            order: 1);
        var fallback = DurableDefinitionFixtures.AiProvider(
            "ai-fallback",
            "Fallback",
            order: 0);
        var preflight = await bundles.PreflightImportAsync(
            Bundle(
                DurableDefinitionFixtures.Document(primary),
                DurableDefinitionFixtures.Document(fallback)),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None);
        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(0, committed.Value!.Inserted);
        Assert.Equal(2, committed.Value.Replaced);
        var stored = await providers.ListAsync(CancellationToken.None);
        Assert.Equal(
            [("ai-fallback", 0), ("ai-primary", 1)],
            stored.Value!
                .Select(item => (item.Value.Id.Value, item.Value.Order))
                .OrderBy(item => item.Order)
                .ToArray());
    }

    [Fact]
    public async Task ImportRejectsCyclicBaseKeymaps()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var first = new KeymapProfile(
            new KeymapProfileId("first-map"),
            "First map",
            KeymapLayer.Application,
            bindings: [],
            basedOn: new KeymapProfileId("second-map"));
        var second = new KeymapProfile(
            new KeymapProfileId("second-map"),
            "Second map",
            KeymapLayer.Application,
            bindings: [],
            basedOn: first.Id);

        var preflight = await bundles.PreflightImportAsync(
            Bundle(
                DurableDefinitionFixtures.Document(first),
                DurableDefinitionFixtures.Document(second)),
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.False(preflight.Value!.CanCommit);
        Assert.Contains(
            preflight.Value.Issues,
            issue => issue.Code == DefinitionImportIssueCode.MissingDependency);
    }

    [Fact]
    public async Task CommitRechecksDependenciesAfterAValidPreflightBecomesStale()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var layout = DurableDefinitionFixtures.Layout();
        Assert.True((await layouts.SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        var bundles = CreateBundleStore(temporary);
        var screen = DurableDefinitionFixtures.Screen();
        var preflight = await bundles.PreflightImportAsync(
            Bundle(DurableDefinitionFixtures.Document(screen)),
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);
        Assert.True(preflight.Value!.CanCommit);
        Assert.True((await layouts.DeleteAsync(
            layout.Key,
            1,
            CancellationToken.None)).IsSuccess);

        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, committed.Error!.Code);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.Empty((await screens.ListAsync(CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task CommitDoesNotTrustForgedPreflightIssues()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var screen = DurableDefinitionFixtures.Screen();
        var bundle = Bundle(DurableDefinitionFixtures.Document(screen));
        var forged = new DefinitionImportPreflight(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            Issues: []);

        var committed = await bundles.CommitImportAsync(forged, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, committed.Error!.Code);
    }

    [Fact]
    public async Task CommitRechecksFailOnConflictAfterPreflight()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var layout = DurableDefinitionFixtures.Layout();
        var bundle = Bundle(DurableDefinitionFixtures.Document(layout));
        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);
        Assert.True(preflight.Value!.CanCommit);
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await layouts.SaveAsync(layout, null, CancellationToken.None)).IsSuccess);

        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, committed.Error!.Code);
        var stored = await layouts.GetAsync(layout.Key, CancellationToken.None);
        Assert.Equal(1, stored.Value!.Revision);
    }

    [Fact]
    public async Task CommitWritesTheExactDocumentsItValidated()
    {
        await using var temporary = TemporaryDatabase.Create();
        var bundles = CreateBundleStore(temporary);
        var valid = DurableDefinitionFixtures.Document(ThemePreference.Default);
        var substituted = valid with
        {
            PayloadJson = InsertJsonProperty(valid.PayloadJson, "\"unvalidated\":true"),
        };
        var definitions = new SwitchingDefinitionList(valid, substituted);
        var bundle = new PortableDefinitionBundle(
            PortableDefinitionBundle.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            definitions);
        var forged = new DefinitionImportPreflight(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            Issues: []);

        var committed = await bundles.CommitImportAsync(forged, CancellationToken.None);
        var exported = await bundles.ExportAsync(CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(1, definitions.EnumerationCount);
        Assert.Equal(valid.PayloadJson, Assert.Single(exported.Value!.Definitions).PayloadJson);
    }

    private static SqliteDefinitionBundleStore CreateBundleStore(TemporaryDatabase temporary) =>
        new(temporary.Database, TimeProvider.System);

    private static PortableDefinitionBundle Bundle(
        params PortableDefinitionDocument[] documents) =>
        new(
            PortableDefinitionBundle.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            documents);

    private static string InsertJsonProperty(string payloadJson, string property) =>
        $"{payloadJson[..^1]},{property}}}";

    private sealed class SwitchingDefinitionList : IReadOnlyList<PortableDefinitionDocument>
    {
        private readonly PortableDefinitionDocument _first;
        private readonly PortableDefinitionDocument _subsequent;

        public SwitchingDefinitionList(
            PortableDefinitionDocument first,
            PortableDefinitionDocument subsequent)
        {
            _first = first;
            _subsequent = subsequent;
        }

        public int Count => 1;

        public int EnumerationCount { get; private set; }

        public PortableDefinitionDocument this[int index] => index == 0
            ? _first
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<PortableDefinitionDocument> GetEnumerator()
        {
            var document = EnumerationCount++ == 0 ? _first : _subsequent;
            yield return document;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
