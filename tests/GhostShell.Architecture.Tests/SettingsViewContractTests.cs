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
            ["DisableStartupProtectionRequested"] = "OnDisableStartupProtectionClick",
            ["EnableStartupProtectionRequested"] = "OnEnableStartupProtectionClick",
            ["FilesSettingsRequested"] = "OnFilesSettingsClick",
            ["LockNowRequested"] = "OnLockNowClick",
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
            ["ReviewHistoryPrivacyRequested"] = "OnReviewHistoryPrivacyClick",
            ["ReviewOnboardingRequested"] = "OnReviewOnboardingClick",
            ["RestoreSessionsOnStartChangedRequested"] =
                "OnRestoreSessionsOnStartChanged",
            ["AppearanceChangedRequested"] = "OnAppearanceChanged",
            ["PickColorRequested"] = "OnPickColorRequested",
            ["SaveKeybindingsRequested"] = "OnSaveKeybindingsClick",
            ["SaveQuickTerminalSettingsRequested"] =
                "OnSaveQuickTerminalSettingsClick",
            ["SaveTerminalProfileRequested"] = "OnSaveTerminalProfileClick",
            ["SecretsSettingsRequested"] = "OnSecretsSettingsClick",
            ["SelectTerminalPaletteRequested"] = "OnSelectTerminalPaletteClick",
            ["SettingsBackRequested"] = "OnSettingsBackClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["TerminalSettingsRequested"] = "OnTerminalSettingsClick",
            ["TestMcpServerRequested"] = "OnTestMcpServerClick",
            ["TitleBarPointerPressedRequested"] = "OnTitleBarPointerPressed",
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
    public void Workspace_settings_expose_the_persisted_session_restore_toggle()
    {
        var settings = LoadView("SettingsView");
        var toggle = Assert.Single(
            settings.Descendants(),
            element => element.Name.LocalName == "ToggleSwitch"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Restore sessions on start",
                    StringComparison.Ordinal));

        Assert.Equal(
            "{Binding RestoreSessionsOnStart, Mode=OneWay}",
            AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{Binding CanChangeRestoreSessionsOnStart}",
            AttributeValue(toggle, "IsEnabled"));
        Assert.Equal(
            "OnRestoreSessionsOnStartChanged",
            AttributeValue(toggle, "IsCheckedChanged"));
    }

    [Fact]
    public void Settings_view_preserves_pages_secure_inputs_and_live_regions()
    {
        var settings = LoadView("SettingsView");
        var root = Assert.IsType<XElement>(settings.Root);
        var surface = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "Grid");

        Assert.Equal("Auto,*", AttributeValue(surface, "RowDefinitions"));
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        var titleBar = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Classes"),
                "TopChrome",
                StringComparison.Ordinal));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Equal(
            "OnTitleBarPointerPressed",
            AttributeValue(titleBar, "PointerPressed"));
        Assert.Equal(
            "{Binding $parent[Window].TitleBarChromeHeight}",
            AttributeValue(titleBar, "MinHeight"));
        Assert.Equal(
            "User",
            AttributeValue(
                FindNamedElement(root, "SettingsBackButton"),
                "WindowDecorationProperties.ElementRole"));
        Assert.Contains(
            "ChromeNavigation",
            AttributeValue(FindNamedElement(root, "SettingsBackButton"), "Classes")
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                ?? []);

        var body = Assert.Single(
            surface.Elements(),
            element => element.Name.LocalName == "Grid"
                && string.Equals(AttributeValue(element, "Grid.Row"), "1", StringComparison.Ordinal));
        Assert.Equal("244,*", AttributeValue(body, "ColumnDefinitions"));
        Assert.Contains(
            body.Elements(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "Classes"),
                    "FloatingSidebar",
                    StringComparison.Ordinal));

        var appearancePage = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "AppearanceSettingsPageView");
        Assert.Equal(
            "AppearanceSettingsPage",
            AttributeValue(appearancePage, "Name"));
        Assert.Equal(
            "{Binding IsAppearanceSettingsVisible}",
            AttributeValue(appearancePage, "IsVisible"));
        // Appearance has no save step; each change is forwarded as it happens.
        Assert.Equal(
            "OnAppearanceChangedRequested",
            AttributeValue(appearancePage, "AppearanceChanged"));
        Assert.Null(AttributeValue(appearancePage, "SaveRequested"));

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
        // Sections are one step apart on the spacing scale. This page and Quick
        // Terminal used to say 22 and 20 for the same intent, which is the drift
        // the scale exists to stop; a literal pinned here would reintroduce it.
        Assert.Equal(
            "{DynamicResource ShellSpaceXl}",
            AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => element.Name.LocalName == "SettingsPageHeader");
        Assert.Equal("Appearance", AttributeValue(header, "Heading"));
        Assert.Equal(
            "Customize how the app looks — colour scheme, accent, and window chrome.",
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
                     ("AppearanceModeSystem", "Color mode System"),
                     ("AppearanceModeDark", "Color mode Dark"),
                     ("AppearanceModeLight", "Color mode Light"),
                     ("PlatformProfilePicker", "Platform profile"),
                     ("AccentModePicker", "Accent mode"),
                     ("CustomAccentText", "Custom accent colour"),
                     ("ApplicationTextScalePicker", "Application text size"),
                 })
        {
            Assert.Equal(
                automationName,
                AttributeValue(
                    FindNamedElement(root, controlName),
                    "AutomationProperties.Name"));
        }

        // The colour-mode tiles are the control: one exclusive group, so the
        // preview a user clicks is the same element that carries the choice.
        foreach (var tileName in new[]
                 {
                     "AppearanceModeSystem",
                     "AppearanceModeDark",
                     "AppearanceModeLight",
                 })
        {
            var tile = FindNamedElement(root, tileName);
            Assert.Equal("RadioButton", tile.Name.LocalName);
            Assert.Equal("AppearanceMode", AttributeValue(tile, "GroupName"));
            Assert.Equal("PresetCard", AttributeValue(tile, "Classes"));
        }

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

        // Appearance applies as you edit, so the page must not carry a save
        // button that implies changes are pending until it is pressed.
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && (AttributeValue(element, "Content") ?? string.Empty)
                    .Contains("Save", StringComparison.Ordinal));

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
            "AppearanceChanged?.Invoke(sender, e);",
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
        // The gap between sections comes from the spacing scale, not from a number
        // written here. A literal is a gap the density and text-scale settings
        // cannot reach, and pinning one in a test is how it would stay that way.
        Assert.Equal(
            "{DynamicResource ShellSpaceXl}",
            AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => element.Name.LocalName == "SettingsPageHeader");
        Assert.Equal("Quick Terminal", AttributeValue(header, "Heading"));
        Assert.Equal(
            "A global drop-down terminal that stays one keystroke away. Configure where it appears and how it behaves.",
            AttributeValue(header, "Description"));

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

        foreach (var automationName in QuickTerminalAutomationNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    automationName,
                    StringComparison.Ordinal));
        }

        var opacity = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "NumericUpDown"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal background opacity",
                    StringComparison.Ordinal));
        Assert.Equal("0", AttributeValue(opacity, "Minimum"));
        Assert.Equal("100", AttributeValue(opacity, "Maximum"));
        Assert.Equal("True", AttributeValue(opacity, "ClipValueToMinMax"));

        var height = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "NumericUpDown"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal panel height",
                    StringComparison.Ordinal));
        Assert.Equal("0", AttributeValue(height, "FormatString"));

        var display = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal display",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding QuickTerminalSettingsEditor.MonitorOptions}",
            AttributeValue(display, "ItemsSource"));
        Assert.Equal(
            "{Binding QuickTerminalSettingsEditor.SelectedMonitorOption}",
            AttributeValue(display, "SelectedItem"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "SettingRow"
                && string.Equals(
                    AttributeValue(element, "Description"),
                    "Active window follows whichever app is in front and falls back to GhostSHELL where unsupported. GhostSHELL window follows this app; Primary always uses the OS primary display.",
                    StringComparison.Ordinal));

        var toggles = root.Descendants()
            .Where(element => element.Name.LocalName == "ToggleSwitch")
            .ToArray();
        Assert.Equal(6, toggles.Length);
        Assert.Equal(
            new[]
            {
                "{Binding QuickTerminalSettingsEditor.IsTranslucent}",
                "{Binding QuickTerminalSettingsEditor.AnimateSlide}",
                "{Binding QuickTerminalSettingsEditor.ReduceMotion}",
                "{Binding QuickTerminalSettingsEditor.HideOnFocusLoss}",
                "{Binding QuickTerminalSettingsEditor.RestoreLastSession}",
                "{Binding QuickTerminalSettingsEditor.RestoreOnStart}",
            },
            toggles
                .Select(toggle => AttributeValue(toggle, "IsChecked"))
                .ToArray());

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.LiveSetting"),
                "Polite",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "{Binding QuickTerminalSettingsEditor.RegistrationStatus}",
                    StringComparison.Ordinal));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class QuickTerminalSettingsPageView");
        Assert.Contains(
            "SaveRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecordHotkeyRequested?.Invoke(sender, e);",
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
        Assert.All(
            textBlocks,
            element => Assert.StartsWith(
                "{DynamicResource ShellLineHeight",
                AttributeValue(element, "LineHeight"),
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
        "AppearanceModeDark",
        "AppearanceModeLight",
        "AppearanceModeSystem",
        "ApplicationTextScalePicker",
        "CustomAccentText",
        "PlatformProfilePicker",
    ];

    private static readonly string[] QuickTerminalAutomationNames =
    [
        "Save Quick Terminal settings",
        "Quick Terminal global hotkey",
        "Record Quick Terminal global hotkey",
        "Quick Terminal display",
        "Quick Terminal panel height",
        "Quick Terminal background opacity",
        "Quick Terminal translucency",
        "Animate Quick Terminal",
        "Reduce Quick Terminal motion",
        "Quick Terminal animation duration",
        "Hide Quick Terminal on focus loss",
        "Keep Quick Terminal session",
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
