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

        var appearancePage = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "AppearanceSettingsPageView");
        Assert.Equal(
            "AppearanceSettingsPage",
            AttributeValue(appearancePage, "Name"));
        Assert.Equal(
            "{Binding IsAppearanceSettingsVisible}",
            AttributeValue(appearancePage, "IsVisible"));
        Assert.Equal(
            "OnAppearanceSaveRequested",
            AttributeValue(appearancePage, "SaveRequested"));

        var quickTerminalPage = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "QuickTerminalSettingsPageView");
        Assert.Equal(
            "QuickTerminalSettingsPage",
            AttributeValue(quickTerminalPage, "Name"));
        Assert.Equal(
            "{Binding IsQuickTerminalSettingsVisible}",
            AttributeValue(quickTerminalPage, "IsVisible"));
        Assert.Equal(
            "OnQuickTerminalSettingsSaveRequested",
            AttributeValue(quickTerminalPage, "SaveRequested"));

        var pageHeaders = root.Descendants()
            .Where(element => element.Name.LocalName == "SettingsPageHeader")
            .ToArray();
        Assert.Equal(InlineSettingsPageVisibilityBindings.Length, pageHeaders.Length);
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

        foreach (var pageVisibility in InlineSettingsPageVisibilityBindings)
        {
            Assert.Contains(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "IsVisible"),
                    $"{{Binding {pageVisibility}}}",
                    StringComparison.Ordinal));
        }

        foreach (var extractedName in SettingsControlNames)
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
    public void Appearance_settings_page_owns_layout_local_behavior_and_typed_seams()
    {
        var appearance = LoadSettingsPage("AppearanceSettingsPageView");
        var root = Assert.IsType<XElement>(appearance.Root);

        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        var content = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "StackPanel");
        Assert.Equal("22", AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => element.Name.LocalName == "SettingsPageHeader");
        Assert.Equal("Appearance", AttributeValue(header, "Heading"));
        Assert.Equal(
            "Follow the host automatically or select an explicit cross-platform profile.",
            AttributeValue(header, "Description"));

        foreach (var controlName in AppearanceControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    controlName,
                    StringComparison.Ordinal));
        }

        foreach (var (controlName, automationName) in new[]
                 {
                     ("AppearanceModePicker", "Color mode"),
                     ("PlatformProfilePicker", "Platform profile"),
                     ("AccentModePicker", "Accent mode"),
                     ("CustomAccentText", "Custom accent color"),
                     ("ApplicationTextScalePicker", "Application text size"),
                 })
        {
            Assert.Equal(
                automationName,
                AttributeValue(
                    FindNamedElement(root, controlName),
                    "AutomationProperties.Name"));
        }

        var appearanceMode = FindNamedElement(root, "AppearanceModePicker");
        Assert.Equal(
            new[] { "System", "Dark", "Light" },
            appearanceMode.Elements()
                .Where(element => element.Name.LocalName == "ComboBoxItem")
                .Select(element => AttributeValue(element, "Content"))
                .ToArray());

        var accentMode = FindNamedElement(root, "AccentModePicker");
        Assert.Equal(
            "OnAccentModeSelectionChanged",
            AttributeValue(accentMode, "SelectionChanged"));
        Assert.Equal(
            new[] { "Follow host", "GhostSHELL bronze", "Custom" },
            accentMode.Elements()
                .Where(element => element.Name.LocalName == "ComboBoxItem")
                .Select(element => AttributeValue(element, "Content"))
                .ToArray());

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    "Save appearance",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnSaveAppearanceClick",
                    StringComparison.Ordinal));
        foreach (var binding in new[] { "ThemeMode", "ThemeProfile", "ThemeTextScale" })
        {
            Assert.Contains(
                root.Descendants(),
                element => (AttributeValue(element, "Text") ?? string.Empty)
                    .Contains(binding, StringComparison.Ordinal));
        }

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class AppearanceSettingsPageView");
        Assert.Contains(
            "SaveRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateCustomAccentAvailability();",
            codeBehind,
            StringComparison.Ordinal);
        foreach (var typedSeam in AppearancePageTypedSeams)
        {
            Assert.Contains(typedSeam, codeBehind, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveThemeAsync", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_settings_page_owns_layout_and_save_relay()
    {
        var quickTerminal = LoadSettingsPage("QuickTerminalSettingsPageView");
        var root = Assert.IsType<XElement>(quickTerminal.Root);

        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        var content = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "StackPanel");
        Assert.Equal("18", AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => element.Name.LocalName == "SettingsPageHeader");
        Assert.Equal("Quick Terminal", AttributeValue(header, "Heading"));

        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Content"),
                    "Save settings",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnSaveQuickTerminalSettingsClick",
                    StringComparison.Ordinal));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class QuickTerminalSettingsPageView");
        Assert.Contains(
            "SaveRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SaveQuickTerminalSettingsAsync",
            codeBehind,
            StringComparison.Ordinal);
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
        Assert.DoesNotContain(
            "OnAccentModeSelectionChanged",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AccentModeSelectionChangedRequested",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    private static readonly string[] InlineSettingsPageVisibilityBindings =
    [
        "IsWorkspaceSettingsVisible",
        "IsKeybindingSettingsVisible",
        "IsFilesSettingsVisible",
        "IsTerminalSettingsVisible",
        "IsSecretsSettingsVisible",
        "IsAgentSettingsVisible",
        "IsMcpSettingsVisible",
        "IsDiagnosticsSettingsVisible",
        "IsAboutSettingsVisible",
    ];

    private static readonly string[] AppearanceControlNames =
    [
        "AccentModePicker",
        "AppearanceModePicker",
        "ApplicationTextScalePicker",
        "CustomAccentText",
        "PlatformProfilePicker",
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
        "CaptureKeybindingPrefixOptions()",
        "CaptureConnectionSecretForm()",
        "CaptureFileProviderSecretForm()",
        "CaptureAiProviderSecretForm()",
        "CaptureMcpServerSecretForm()",
        "BindOperationalViewModels(",
        "FocusBackButton()",
        "FocusSavedScreenUndo()",
    ];

    private static readonly string[] AppearancePageTypedSeams =
    [
        "ConfigureAppearanceControls(",
        "ApplyAppearance(",
        "CaptureAppearance()",
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

    private static readonly string[] SettingsControlNames =
    [
        "AiProviderSecretLabelInput",
        "AiProviderSecretProfilePicker",
        "AiProviderSecretValueInput",
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

    private static readonly string[] ExtractedControlNames =
    [
        .. AppearanceControlNames,
        .. SettingsControlNames,
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

    private static XDocument LoadSettingsPage(string page) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "SettingsPages",
            $"{page}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
