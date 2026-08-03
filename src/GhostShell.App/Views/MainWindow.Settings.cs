using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GhostShell.App;
using GhostShell.App.Views.SettingsPages;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    internal static IReadOnlyList<PlatformProfile> AppearancePlatformProfiles { get; } =
        Enum.GetValues<PlatformProfile>();

    internal static IReadOnlyList<AppearanceTextScaleOption> AppearanceTextScaleOptions { get; } =
    [
        new("Follow host", null),
        new("100%", 1),
        new("125%", 1.25),
        new("150%", 1.5),
        new("175%", 1.75),
        new("200%", 2),
        new("250%", 2.5),
    ];

    private bool _changingKeybindingProfile;
    private ThemePreference? _appearanceControlsSource;

    private SettingsView SettingsRoute =>
        this.FindControl<SettingsView>("SettingsRouteView")
        ?? throw new InvalidOperationException(
            "The settings route view is unavailable.");

    public void NavigateToSettings(SettingsPage page = SettingsPage.Appearance)
    {
        ViewModel.ShowSettings(page);
        if (ViewModel.IsSettingsVisible && !ViewModel.HasOverlay)
        {
            FocusSettingsWhenReady(static settings => settings.FocusBackButton());
        }
    }

    private void OnSettingsBackClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.HasRuntimeWorkspace)
        {
            ViewModel.ShowWorkspace();
            FocusActivePanel();
        }
        else
        {
            NavigateToLauncher();
        }
    }

    private void OnAppearanceSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Appearance);

    private void OnWorkspaceSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Workspaces);

    private async void OnRestoreSessionsOnStartChanged(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is not ToggleSwitch toggle
            || !ViewModel.CanChangeRestoreSessionsOnStart)
        {
            return;
        }

        await ViewModel.SetRestoreSessionsOnStartAsync(
            toggle.IsChecked == true,
            CancellationToken.None);
    }

    private void OnKeybindingSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Keybindings);

    private void OnFilesSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Files);

    private void OnTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Terminal);

    private void OnQuickTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.QuickTerminal);

    private void OnSecretsSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Secrets);

    private void OnDiagnosticsSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Diagnostics);

    private void OnAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Agent);

    private void OnMcpSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.Mcp);

    private void OnAboutSettingsClick(object? sender, RoutedEventArgs e) =>
        SetSettingsPage(SettingsPage.About);

    private void SetSettingsPage(SettingsPage page)
    {
        ViewModel.SettingsPage = page;
        ViewModel.ShowSettings(page);
        if (page == SettingsPage.Appearance)
        {
            RefreshAppearanceControlsFromStoredProfile();
        }
    }

    private void OnOpenThirdPartyNoticesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var path = ProductDocumentLocator.FindThirdPartyNotices();
        if (path is null)
        {
            ViewModel.SetError(
                "The bundled third-party notices could not be found. Reinstall this build.");
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            if (process is null)
            {
                ViewModel.SetError(
                    "The operating system did not provide an application for the notices file.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ViewModel.SetError(
                "The operating system could not open the bundled third-party notices.");
        }
    }

    internal void RefreshAppearanceControlsFromStoredProfile()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var theme = viewModel.ActiveTheme;
        if (_appearanceControlsSource == theme)
        {
            return;
        }

        _applyingAppearanceControls = true;
        try
        {
            SettingsRoute.ApplyAppearance(
                theme,
                ResolveApplicationTextScaleOption(theme.TextScaleOverride));
        }
        finally
        {
            _applyingAppearanceControls = false;
        }

        _appearanceControlsSource = theme;
    }

    internal static AppearanceTextScaleOption ResolveApplicationTextScaleOption(
        double? textScale)
    {
        var standard = AppearanceTextScaleOptions.FirstOrDefault(option =>
            option.Scale == textScale);
        return standard ?? new(
            textScale!.Value.ToString("0.##%", CultureInfo.InvariantCulture),
            textScale);
    }

    private async void OnKeybindingProfileSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        _ = e;
        if (_changingKeybindingProfile
            || sender is not ComboBox
            {
                SelectedItem: KeybindingProfileItemViewModel selected,
            } selector
            || selected.Id == ViewModel.KeybindingEditorSession?.ProfileId)
        {
            return;
        }

        if (ViewModel.KeybindingEditorSession?.IsDirty == true
            && !await new DiscardChangesDialog(
                    "Discard keybinding changes?",
                    "The unsaved shortcuts, prefix, and conflict resolutions will be lost.")
                .ShowDialog<bool>(this))
        {
            _changingKeybindingProfile = true;
            selector.SelectedItem = ViewModel.SelectedKeybindingProfile;
            _changingKeybindingProfile = false;
            return;
        }

        ViewModel.SelectKeybindingProfile(selected);
    }

    private void OnCloneKeybindingPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.CloneSelectedKeybindingProfile();
    }

    private async void OnRecordKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: KeybindingEditorRowItemViewModel row }
            || ViewModel.KeybindingEditorSession is not { } editor)
        {
            return;
        }

        var maximumStrokes = editor.Layer == KeymapLayer.Terminal
            ? 1
            : ShortcutRecorderDialog.DefaultMaximumStrokes;
        var sequence = await new ShortcutRecorderDialog(row.Row.Sequence, maximumStrokes)
            .ShowDialog<KeySequence?>(this);
        if (sequence is not null)
        {
            editor.RecordShortcut(row.Id, sequence.Strokes);
        }
    }

    private void OnUnbindKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: KeybindingEditorRowItemViewModel row })
        {
            ViewModel.KeybindingEditorSession?.Unbind(row.Id);
        }
    }

    private void OnResetKeybindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: KeybindingEditorRowItemViewModel row })
        {
            ViewModel.KeybindingEditorSession?.ResetShortcut(row.Id);
        }
    }

    private async void OnRecordKeybindingPrefixClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.KeybindingEditorSession is not { CanEditPrefix: true } editor)
        {
            return;
        }

        var recorded = await new ShortcutRecorderDialog(initial: null, maximumStrokes: 1)
            .ShowDialog<KeySequence?>(this);
        if (recorded is null)
        {
            return;
        }

        if (recorded.Count != 1)
        {
            ViewModel.SetError("An application prefix must contain exactly one key stroke.");
            return;
        }

        editor.RecordPrefix(recorded[0]);
    }

    private void OnClearKeybindingPrefixClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.KeybindingEditorSession is { CanEditPrefix: true } editor)
        {
            editor.ClearPrefix();
        }
    }

    private void OnKeybindingPrefixOptionsChanged(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { IsKeyboardFocusWithin: true }
            || ViewModel.KeybindingEditorSession is not
            {
                CanEditPrefix: true,
                HasPrefix: true,
            } editor
            || SettingsRoute.CaptureKeybindingPrefixOptions()
                is not { } options)
        {
            return;
        }

        editor.UpdatePrefixOptions(
            options.TimeoutMilliseconds,
            options.Repeatable,
            options.FailureBehavior);
    }

    private void OnResetAllKeybindingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.KeybindingEditorSession?.ResetAll();
    }

    private async void OnSaveKeybindingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveKeybindingEditorAsync(_lifetime.Token);
    }

    private async void OnAddFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowFileProviderEditorAsync(null);
    }

    private async void OnEditFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileProviderProfileItemViewModel profile })
        {
            await ShowFileProviderEditorAsync(profile.Id);
        }
    }

    private async void OnDeleteFileProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: FileProviderProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("file provider", profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(FileProviderProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowFileProviderEditorAsync(FileProviderProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Files,
                fileProfileId: profileId,
                initialFamily: SavedConnectionFamily.Files);
            var result = await new ConnectionEditorDialog(editor)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is UnifiedConnectionEditorResult.Files files)
            {
                _ = await ViewModel.SaveFileProviderProfileAsync(files.Request, _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnAddAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowAiProviderEditorAsync(null);
    }

    private async void OnEditAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: AiProviderProfileItemViewModel profile })
        {
            await ShowAiProviderEditorAsync(profile.Id);
        }
    }

    private async void OnDeleteAiProviderClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: AiProviderProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("AI provider", profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(AiProviderProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowAiProviderEditorAsync(AiProviderProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateAiProviderEditor(profileId);
            var request = await new AiProviderProfileEditorDialog(editor)
                .ShowDialog<AiProviderProfileSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveAiProviderProfileAsync(request, _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnAddMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowMcpServerEditorAsync(null);
    }

    private async void OnEditMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpServerProfileItemViewModel profile })
        {
            await ShowMcpServerEditorAsync(profile.Id);
        }
    }

    private async void OnTestMcpServerClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control
            {
                DataContext: McpServerProfileItemViewModel profile,
            } testControl)
        {
            await ViewModel.TestMcpServerAsync(
                profile,
                _lifetime.Token);
            if (testControl.IsEnabled)
            {
                _ = testControl.Focus();
            }
        }
    }

    private async void OnDeleteMcpServerClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: McpServerProfileItemViewModel profile })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog(
                "MCP server",
                profile.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(McpServerProfile.Kind, profile.Id.Value),
            profile.Revision,
            _lifetime.Token);
    }

    private async Task ShowMcpServerEditorAsync(McpServerProfileId? profileId)
    {
        try
        {
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateMcpServerEditor(profileId);
            var request = await new McpServerProfileEditorDialog(editor)
                .ShowDialog<McpServerProfileSaveRequest?>(this);
            if (request is not null)
            {
                _ = await ViewModel.SaveMcpServerProfileAsync(
                    request,
                    _lifetime.Token);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    /// <summary>
    /// The palette field waiting for a sampled colour, or null when the
    /// eyedropper is not armed.
    /// </summary>
    private string? _pendingColorSampleField;

    private async void OnPickColorRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { Tag: string field })
        {
            return;
        }

        // Prefer the host's own screen picker so a colour can be lifted from
        // anywhere, not only from this window.
        if (_screenColorSampler is { IsAvailable: true } sampler)
        {
            try
            {
                var picked = await sampler.SampleAsync(_lifetime.Token);
                if (picked is { } screenColor)
                {
                    ApplySampledColor(field, screenColor);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }

            return;
        }

        // Without a host picker, sampling falls back to this window: the next
        // click chooses the colour, so one can still be lifted from terminal
        // output or the preview.
        _pendingColorSampleField = field;
        Cursor = new Cursor(StandardCursorType.Cross);
        ViewModel.ShowApplicationKeySequenceHint(
            $"Click anywhere to sample the {field.ToLowerInvariant()} colour · Esc to cancel");
        AddHandler(PointerPressedEvent, OnColorSamplePointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnColorSampleKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnColorSamplePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (_pendingColorSampleField is not { } field)
        {
            return;
        }

        e.Handled = true;
        var sampled = ColorSampling.Sample(this, e.GetPosition(this));
        EndColorSample();
        if (sampled is not { } color)
        {
            return;
        }

        ApplySampledColor(field, color);
    }

    /// <summary>
    /// The workspace editor's eyedropper reaches the same sampling path as the
    /// palette's, so screen picking behaves identically wherever a colour is
    /// chosen. The editor raises an intent rather than sampling itself, because
    /// screen capture is a host capability.
    /// </summary>
    private void OnWorkspaceAccentPickRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        OnPickColorRequested(
            new Border { Tag = WorkspaceAccentSampleField },
            new RoutedEventArgs());
    }

    private const string WorkspaceAccentSampleField = "WorkspaceAccent";

    private void ApplySampledColor(string field, Avalonia.Media.Color color)
    {
        if (field == WorkspaceAccentSampleField)
        {
            this.FindControl<WorkspaceEditorView>("WorkspaceDefinitionEditor")
                ?.ApplySampledAccent(color);
            return;
        }

        // The accent lives on the theme, not the terminal profile, so it is
        // written through the page that owns its field.
        if (field == "Accent")
        {
            SettingsRoute.SetCustomAccent(color);
            return;
        }

        if (ViewModel.TerminalSettingsEditor is not { } editor)
        {
            return;
        }

        switch (field)
        {
            case "Background": editor.BackgroundColor = color; break;
            case "Foreground": editor.ForegroundColor = color; break;
            case "Cursor": editor.CursorColor = color; break;
            case "Selection": editor.SelectionColor = color; break;
            default: return;
        }

        OnAppearanceChanged(this, new RoutedEventArgs());
    }

    private void OnColorSampleKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (_pendingColorSampleField is null || e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        EndColorSample();
    }

    private void EndColorSample()
    {
        _pendingColorSampleField = null;
        Cursor = Cursor.Default;
        RemoveHandler(PointerPressedEvent, OnColorSamplePointerPressed);
        RemoveHandler(KeyDownEvent, OnColorSampleKeyDown);
        ViewModel.ClearApplicationKeySequenceHint();
    }

    /// <summary>
    /// Appearance has no save button, so edits are coalesced briefly before they
    /// reach the store. Dragging a slider would otherwise write once per pixel,
    /// and every write reapplies the whole theme.
    /// </summary>
    private DispatcherTimer? _appearanceCommitTimer;

    /// <summary>
    /// Set while the page is being filled in from the stored profile, so that
    /// writing a value into a control does not read as the user editing it.
    ///
    /// Without this the page commits in a loop: a commit saves the theme, saving
    /// notifies the catalog, the notification refills the controls, and refilling
    /// raises the very change events that schedule the next commit. It settles at
    /// about eight writes a second and pins a core.
    /// </summary>
    private bool _applyingAppearanceControls;

    private void OnAppearanceChanged(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_applyingAppearanceControls)
        {
            return;
        }

        _appearanceCommitTimer ??= CreateAppearanceCommitTimer();
        _appearanceCommitTimer.Stop();
        _appearanceCommitTimer.Start();
    }

    private DispatcherTimer CreateAppearanceCommitTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await CommitAppearanceAsync();
        };
        return timer;
    }

    private async Task CommitAppearanceAsync()
    {
        try
        {
            var selection = SettingsRoute.CaptureAppearance();
            var result = await ViewModel.SaveThemeAsync(
                selection.Appearance,
                selection.PlatformProfile,
                selection.Accent,
                selection.TextScale,
                _lifetime.Token,
                new ThemeChromePreference(
                    selection.CornerRadius,
                    selection.Density,
                    selection.ShowTabBar,
                    selection.ShowWorkspacesPanel,
                    selection.TabStripPlacement,
                    selection.WorkspacePanelPlacement));
            if (!result.IsSuccess)
            {
                return;
            }

            // The page also edits the terminal palette, font, and cursor, which
            // live on the terminal profile. Saving only the theme would discard
            // everything the page shows below Theme.
            if (ViewModel.TerminalSettingsEditor is not null)
            {
                _ = await ViewModel.SaveTerminalProfileAsync(_lifetime.Token);
            }

            // The controls are already showing what was just captured from them,
            // so there is nothing to refill — only the record of what they show.
            _appearanceControlsSource = ViewModel.ActiveTheme;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or InvalidOperationException)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    /// <summary>
    /// Applying a preset only rewrites the open editor; it reaches the store when
    /// the user chooses Save profile, like every other field on the page.
    /// </summary>
    private void OnSelectTerminalPaletteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: TerminalPaletteOption option })
        {
            ViewModel.TerminalSettingsEditor?.ApplyPalettePreset(option.Palette);
        }
    }

    private async void OnSaveTerminalProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveTerminalProfileAsync(_lifetime.Token);
    }

    private async void OnSaveQuickTerminalSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.SaveQuickTerminalSettingsAsync(_lifetime.Token);
    }

    private async void OnCreateConnectionSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureConnectionSecretForm();
        if (input.Connection is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose a connection and credential kind.");
            return;
        }

        var created = await ViewModel.CreateConnectionSecretAsync(
            input.Connection.Id,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearConnectionSecretValue();
        if (created)
        {
            SettingsRoute.ClearConnectionSecretLabel();
        }
    }

    private async void OnCreateFileProviderSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureFileProviderSecretForm();
        if (input.Profile is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose a file provider and credential kind.");
            return;
        }

        var created = await ViewModel.CreateFileProviderSecretAsync(
            input.Profile.Id,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearFileProviderSecretValue();
        if (created)
        {
            SettingsRoute.ClearFileProviderSecretLabel();
        }
    }

    private async void OnCreateAiProviderSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureAiProviderSecretForm();
        if (input.Profile is null)
        {
            ViewModel.SetError("Choose an AI-provider profile.");
            return;
        }

        var created = await ViewModel.CreateAiProviderSecretAsync(
            input.Profile.Id,
            input.Label,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearAiProviderSecretValue();
        if (created)
        {
            SettingsRoute.ClearAiProviderSecretLabel();
        }
    }

    private async void OnCreateMcpServerSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = SettingsRoute.CaptureMcpServerSecretForm();
        if (input.Target is null || input.Kind is not { } secretKind)
        {
            ViewModel.SetError("Choose an MCP environment binding and credential kind.");
            return;
        }

        var created = await ViewModel.CreateMcpServerSecretAsync(
            input.Target,
            input.Label,
            secretKind,
            input.Value,
            _lifetime.Token);
        SettingsRoute.ClearMcpServerSecretValue();
        if (created)
        {
            SettingsRoute.ClearMcpServerSecretLabel();
        }
    }

    private async void OnDeleteSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: SecretMetadataViewModel secret })
        {
            return;
        }

        var confirmed = await new DefinitionDeleteDialog("credential", secret.Label)
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            _ = await ViewModel.DeleteSecretAsync(secret, _lifetime.Token);
        }
    }

    private async void OnEditSecretClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: SecretMetadataViewModel secret })
        {
            return;
        }

        var request = await new SecretEditorDialog(new SecretEditorViewModel(secret))
            .ShowDialog<SecretEditRequest?>(this);
        if (request is null)
        {
            return;
        }

        using var replacement = request.Replacement;
        if (request.Action == SecretEditAction.Relabel)
        {
            _ = await ViewModel.RelabelSecretAsync(
                secret,
                request.Label,
                _lifetime.Token);
        }
        else if (replacement is not null)
        {
            _ = await ViewModel.ReplaceSecretAsync(
                secret,
                replacement,
                _lifetime.Token);
        }
    }

    private async void OnCancelFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileTransferItemViewModel transfer })
        {
            _ = await ViewModel.CancelFileTransferAsync(transfer.Id, _lifetime.Token);
        }
    }

    private async void OnRetryFileTransferClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: FileTransferItemViewModel transfer })
        {
            _ = await ViewModel.RetryFileTransferAsync(transfer.Id, _lifetime.Token);
        }
    }

    private void FocusSettingsWhenReady(Action<SettingsView> focus) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            focus(SettingsRoute));
}
