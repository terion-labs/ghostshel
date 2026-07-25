using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class SettingsViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> ShellInteractions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AboutSettingsRequested"] = "OnAboutSettingsClick",
            ["AccentModeSelectionChangedRequested"] = "OnAccentModeSelectionChanged",
            ["AddAiProviderRequested"] = "OnAddAiProviderClick",
            ["AddFileProviderRequested"] = "OnAddFileProviderClick",
            ["AddMcpServerRequested"] = "OnAddMcpServerClick",
            ["AgentSettingsRequested"] = "OnAgentSettingsClick",
            ["AppearanceSettingsRequested"] = "OnAppearanceSettingsClick",
            ["CancelFileTransferRequested"] = "OnCancelFileTransferClick",
            ["ClearKeybindingPrefixRequested"] = "OnClearKeybindingPrefixClick",
            ["CloneKeybindingPresetRequested"] = "OnCloneKeybindingPresetClick",
            ["CreateAiProviderSecretRequested"] = "OnCreateAiProviderSecretClick",
            ["CreateConnectionSecretRequested"] = "OnCreateConnectionSecretClick",
            ["CreateFileProviderSecretRequested"] = "OnCreateFileProviderSecretClick",
            ["CreateMcpServerSecretRequested"] = "OnCreateMcpServerSecretClick",
            ["DeleteAiProviderRequested"] = "OnDeleteAiProviderClick",
            ["DeleteFileProviderRequested"] = "OnDeleteFileProviderClick",
            ["DeleteMcpServerRequested"] = "OnDeleteMcpServerClick",
            ["DeleteScreenRequested"] = "OnDeleteScreenClick",
            ["DeleteSecretRequested"] = "OnDeleteSecretClick",
            ["DeleteWorkspaceRequested"] = "OnDeleteWorkspaceClick",
            ["DiagnosticsSettingsRequested"] = "OnDiagnosticsSettingsClick",
            ["DismissSavedScreenDeleteUndoRequested"] =
                "OnDismissSavedScreenDeleteUndoClick",
            ["EditAiProviderRequested"] = "OnEditAiProviderClick",
            ["EditFileProviderRequested"] = "OnEditFileProviderClick",
            ["EditLayoutRequested"] = "OnEditLayoutClick",
            ["EditMcpServerRequested"] = "OnEditMcpServerClick",
            ["EditScreenRequested"] = "OnEditScreenClick",
            ["EditSecretRequested"] = "OnEditSecretClick",
            ["EditWorkspaceRequested"] = "OnEditWorkspaceClick",
            ["ExportDefinitionsRequested"] = "OnExportDefinitionsClick",
            ["FilesSettingsRequested"] = "OnFilesSettingsClick",
            ["ImportDefinitionsRequested"] = "OnImportDefinitionsClick",
            ["KeybindingPrefixOptionsChangedRequested"] =
                "OnKeybindingPrefixOptionsChanged",
            ["KeybindingProfileSelectionChangedRequested"] =
                "OnKeybindingProfileSelectionChanged",
            ["KeybindingSettingsRequested"] = "OnKeybindingSettingsClick",
            ["McpSettingsRequested"] = "OnMcpSettingsClick",
            ["OpenThirdPartyNoticesRequested"] = "OnOpenThirdPartyNoticesClick",
            ["QuickTerminalSettingsRequested"] = "OnQuickTerminalSettingsClick",
            ["RecordKeybindingPrefixRequested"] = "OnRecordKeybindingPrefixClick",
            ["RecordKeybindingRequested"] = "OnRecordKeybindingClick",
            ["ResetAllKeybindingsRequested"] = "OnResetAllKeybindingsClick",
            ["ResetKeybindingRequested"] = "OnResetKeybindingClick",
            ["RetryFileTransferRequested"] = "OnRetryFileTransferClick",
            ["ReviewHistoryPrivacyRequested"] = "OnReviewHistoryPrivacyClick",
            ["ReviewOnboardingRequested"] = "OnReviewOnboardingClick",
            ["SaveAppearanceRequested"] = "OnSaveAppearanceClick",
            ["SaveKeybindingsRequested"] = "OnSaveKeybindingsClick",
            ["SaveQuickTerminalSettingsRequested"] =
                "OnSaveQuickTerminalSettingsClick",
            ["SaveTerminalProfileRequested"] = "OnSaveTerminalProfileClick",
            ["SecretsSettingsRequested"] = "OnSecretsSettingsClick",
            ["SettingsBackRequested"] = "OnSettingsBackClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["TerminalSettingsRequested"] = "OnTerminalSettingsClick",
            ["TestMcpServerRequested"] = "OnTestMcpServerClick",
            ["UnbindKeybindingRequested"] = "OnUnbindKeybindingClick",
            ["UndoDeletedSavedScreenRequested"] =
                "OnUndoDeletedSavedScreenClick",
            ["WorkspaceSettingsRequested"] = "OnWorkspaceSettingsClick",
        };

    [Fact]
    public void Main_window_delegates_the_settings_route_to_one_named_view()
    {
        var mainWindow = LoadView("MainWindow");
        var settings = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "SettingsView");

        Assert.Equal("SettingsRouteView", AttributeValue(settings, "Name"));
        Assert.Equal(
            "{Binding IsSettingsVisible}",
            AttributeValue(settings, "IsVisible"));

        foreach (var (interaction, handler) in ShellInteractions)
        {
            Assert.Equal(handler, AttributeValue(settings, interaction));
        }

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Settings_view_preserves_pages_secure_inputs_and_live_regions()
    {
        var settings = LoadView("SettingsView");
        var root = Assert.IsType<XElement>(settings.Root);
        var surface = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Grid");

        Assert.Equal("44,*", AttributeValue(surface, "RowDefinitions"));
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        var body = Assert.Single(
            surface.Elements(),
            element => element.Name.LocalName == "Grid"
                && string.Equals(AttributeValue(element, "Grid.Row"), "1", StringComparison.Ordinal));
        Assert.Equal("244,*", AttributeValue(body, "ColumnDefinitions"));

        var pageHeaders = root.Descendants()
            .Where(element => element.Name.LocalName == "SettingsPageHeader")
            .ToArray();
        Assert.Equal(SettingsPageVisibilityBindings.Length, pageHeaders.Length);
        Assert.All(pageHeaders, header =>
        {
            Assert.False(string.IsNullOrWhiteSpace(AttributeValue(header, "Heading")));
            Assert.False(string.IsNullOrWhiteSpace(AttributeValue(header, "Description")));
        });
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Headless mode and ACP will use these same definitions in a later milestone.",
                StringComparison.Ordinal));

        foreach (var pageVisibility in SettingsPageVisibilityBindings)
        {
            Assert.Contains(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "IsVisible"),
                    $"{{Binding {pageVisibility}}}",
                    StringComparison.Ordinal));
        }

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        foreach (var passwordInput in PasswordInputNames)
        {
            var input = FindNamedElement(root, passwordInput);
            Assert.Equal("TextBox", input.Name.LocalName);
            Assert.Equal("●", AttributeValue(input, "PasswordChar"));
        }

        var undoNotice = FindNamedElement(root, "SavedScreenDeleteUndoNotice");
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.HasPending}",
            AttributeValue(undoNotice, "IsVisible"));
        Assert.Equal(
            "Polite",
            AttributeValue(undoNotice, "AutomationProperties.LiveSetting"));

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "RecoveryDataControlView");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "LocalArtifactControlView");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "DiagnosticsExportView");
    }

    [Fact]
    public void Settings_page_header_is_a_passive_shared_component()
    {
        var header = LoadComponent("SettingsPageHeader");
        var root = Assert.IsType<XElement>(header.Root);

        Assert.Equal("Root", AttributeValue(root, "Name"));
        Assert.Equal("False", AttributeValue(root, "Focusable"));

        var textBlocks = root.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .ToArray();
        Assert.Equal(2, textBlocks.Length);
        Assert.Contains(
            textBlocks,
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding Heading, ElementName=Root}",
                StringComparison.Ordinal));
        Assert.Contains(
            textBlocks,
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding Description, ElementName=Root}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_view_forwards_interactions_and_exposes_typed_form_seams()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class SettingsView");

        foreach (var interaction in ShellInteractions.Keys)
        {
            Assert.Contains($" {interaction};", codeBehind, StringComparison.Ordinal);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        foreach (var typedSeam in TypedPresentationSeams)
        {
            Assert.Contains(typedSeam, codeBehind, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("FindControl<", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(".Start()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_uses_the_typed_settings_bridge_and_keeps_effect_ownership()
    {
        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");

        foreach (var typedCall in MainWindowTypedCalls)
        {
            Assert.Contains(typedCall, mainWindowCode, StringComparison.Ordinal);
        }

        Assert.Contains(
            "this.FindControl<SettingsView>(\"SettingsRouteView\")",
            mainWindowCode,
            StringComparison.Ordinal);

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                $"FindControl<Control>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"FindControl<ComboBox>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"FindControl<TextBox>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "new AvaloniaDiagnosticsBundleDestinationPicker(this)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("recoveryDataControlViewModel.Start();", mainWindowCode);
        Assert.Contains("localArtifactControlViewModel.Start();", mainWindowCode);
        Assert.Contains("ShowDialog<", mainWindowCode, StringComparison.Ordinal);
    }

    private static readonly string[] SettingsPageVisibilityBindings =
    [
        "IsAppearanceSettingsVisible",
        "IsWorkspaceSettingsVisible",
        "IsKeybindingSettingsVisible",
        "IsFilesSettingsVisible",
        "IsTerminalSettingsVisible",
        "IsQuickTerminalSettingsVisible",
        "IsSecretsSettingsVisible",
        "IsAgentSettingsVisible",
        "IsMcpSettingsVisible",
        "IsDiagnosticsSettingsVisible",
        "IsAboutSettingsVisible",
    ];

    private static readonly string[] PasswordInputNames =
    [
        "SecretValueInput",
        "FileProviderSecretValueInput",
        "AiProviderSecretValueInput",
        "McpServerSecretValueInput",
    ];

    private static readonly string[] TypedPresentationSeams =
    [
        "ConfigureAppearanceControls(",
        "ApplyAppearance(",
        "CaptureAppearance()",
        "UpdateCustomAccentAvailability()",
        "CaptureKeybindingPrefixOptions()",
        "CaptureConnectionSecretForm()",
        "CaptureFileProviderSecretForm()",
        "CaptureAiProviderSecretForm()",
        "CaptureMcpServerSecretForm()",
        "BindOperationalViewModels(",
        "FocusBackButton()",
        "FocusSavedScreenUndo()",
    ];

    private static readonly string[] MainWindowTypedCalls =
    [
        "SettingsRoute.ConfigureAppearanceControls(",
        "SettingsRoute.BindOperationalViewModels(",
        "SettingsRoute.ApplyAppearance(",
        "SettingsRoute.CaptureAppearance()",
        "SettingsRoute.CaptureKeybindingPrefixOptions()",
        "SettingsRoute.CaptureConnectionSecretForm()",
        "SettingsRoute.CaptureFileProviderSecretForm()",
        "SettingsRoute.CaptureAiProviderSecretForm()",
        "SettingsRoute.CaptureMcpServerSecretForm()",
        "settings.FocusBackButton()",
        "settings.FocusSavedScreenUndo()",
    ];

    private static readonly string[] ExtractedControlNames =
    [
        "AccentModePicker",
        "AiProviderSecretLabelInput",
        "AiProviderSecretProfilePicker",
        "AiProviderSecretValueInput",
        "AppearanceModePicker",
        "ApplicationTextScalePicker",
        "CustomAccentText",
        "DiagnosticsExportView",
        "FileProviderSecretKindPicker",
        "FileProviderSecretLabelInput",
        "FileProviderSecretValueInput",
        "KeybindingPrefixFailure",
        "KeybindingPrefixRepeatable",
        "KeybindingPrefixTimeout",
        "LocalArtifactControlView",
        "McpEnvironmentSecretTargetPicker",
        "McpServerSecretKindPicker",
        "McpServerSecretLabelInput",
        "McpServerSecretValueInput",
        "McpSettingsSection",
        "PlatformProfilePicker",
        "RecoveryDataControlView",
        "SavedScreenDeleteUndoNotice",
        "SecretConnectionPicker",
        "SecretFileProviderPicker",
        "SecretKindPicker",
        "SecretLabelInput",
        "SecretValueInput",
        "SettingsBackButton",
        "UndoDeletedSavedScreenButton",
    ];

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string component) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            $"{component}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
