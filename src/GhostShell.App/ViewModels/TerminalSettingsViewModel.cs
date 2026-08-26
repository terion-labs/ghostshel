using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns the revisioned terminal and Quick Terminal settings drafts. Runtime
/// terminal refresh and operating-system shortcut registration stay with the host.
/// </summary>
public sealed class TerminalSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private DefinitionCatalogSnapshot _snapshot;
    private TerminalProfileEditorViewModel? _terminalEditor;
    private QuickTerminalSettingsEditorViewModel? _quickTerminalEditor;
    private bool _disposed;

    public TerminalSettingsViewModel(IDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _snapshot = _catalog.Snapshot;
        ApplyCatalog(_snapshot);
    }

    public TerminalProfileEditorViewModel? TerminalEditor
    {
        get => _terminalEditor;
        private set => SetProperty(ref _terminalEditor, value);
    }

    public QuickTerminalSettingsEditorViewModel? QuickTerminalEditor
    {
        get => _quickTerminalEditor;
        private set => SetProperty(ref _quickTerminalEditor, value);
    }

    public TerminalProfile? ActiveTerminalProfile =>
        _snapshot.TerminalProfiles.FirstOrDefault()?.Value;

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        var previousActive = ActiveTerminalProfile;
        _snapshot = snapshot;
        ApplyTerminalProfile(snapshot);
        ApplyQuickTerminalSettings(snapshot);
        if (previousActive != ActiveTerminalProfile)
        {
            OnPropertyChanged(nameof(ActiveTerminalProfile));
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
        SaveTerminalProfileAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (TerminalEditor is null)
        {
            return Fail<StoredDefinition<TerminalProfile>>(
                "No terminal profile is available to edit.");
        }

        TerminalProfileEditorSaveRequest request;
        try
        {
            request = TerminalEditor.CreateSaveRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Fail<StoredDefinition<TerminalProfile>>(exception.Message);
        }

        // A no-op save must not publish a catalog change. Rebinding the editor
        // after that notification looks like another fresh edit to the view.
        if (ActiveTerminalProfile is { } stored
            && stored.RepresentsSameAs(request.Profile))
        {
            return DefinitionStoreResult<StoredDefinition<TerminalProfile>>.Success(
                new StoredDefinition<TerminalProfile>(
                    stored,
                    request.ExpectedRevision,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch));
        }

        return await _catalog.SaveTerminalProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
        SaveQuickTerminalSettingsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (QuickTerminalEditor is null)
        {
            return Fail<StoredDefinition<QuickTerminalSettings>>(
                "Quick Terminal settings are unavailable.");
        }

        QuickTerminalSettingsSaveRequest request;
        try
        {
            request = QuickTerminalEditor.CreateSaveRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Fail<StoredDefinition<QuickTerminalSettings>>(exception.Message);
        }

        return await _catalog.SaveQuickTerminalSettingsAsync(
            request.Settings,
            request.ExpectedRevision,
            cancellationToken);
    }

    public void ApplyQuickTerminalRegistration(
        KeyStroke configuredGesture,
        KeyStroke? activeGesture,
        GlobalHotkeyRegistrationResult result)
    {
        ThrowIfDisposed();
        QuickTerminalEditor?.ApplyRegistration(
            configuredGesture,
            activeGesture,
            result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TerminalEditor = null;
        QuickTerminalEditor = null;
    }

    private void ApplyTerminalProfile(DefinitionCatalogSnapshot snapshot)
    {
        var terminal = snapshot.TerminalProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (terminal is null)
        {
            TerminalEditor = null;
            return;
        }

        if (TerminalEditor is not null
            && TerminalEditor.ProfileId == terminal.Value.Id
            && TerminalEditor.ExpectedRevision == terminal.Revision
            && TerminalEditor.MatchesTerminalKeymaps(snapshot.Keymaps.Select(item => item.Value)))
        {
            return;
        }

        TerminalEditor = new TerminalProfileEditorViewModel(
            terminal.Value,
            terminal.Revision,
            snapshot.Keymaps.Select(item => item.Value));
    }

    private void ApplyQuickTerminalSettings(DefinitionCatalogSnapshot snapshot)
    {
        var quickTerminal = snapshot.QuickTerminalSettings
            .OrderByDescending(item => item.Value.Id == QuickTerminalSettings.DefaultId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (quickTerminal is null)
        {
            QuickTerminalEditor = null;
            return;
        }

        if (QuickTerminalEditor is not null
            && QuickTerminalEditor.SettingsId == quickTerminal.Value.Id
            && QuickTerminalEditor.ExpectedRevision == quickTerminal.Revision)
        {
            return;
        }

        QuickTerminalEditor = new QuickTerminalSettingsEditorViewModel(
            quickTerminal.Value,
            quickTerminal.Revision);
    }

    private static DefinitionStoreResult<T> Fail<T>(string message) =>
        DefinitionStoreResult<T>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
