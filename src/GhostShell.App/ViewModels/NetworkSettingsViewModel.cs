using System.Collections.ObjectModel;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record NetworkConnectionProfileItemViewModel(
    NetworkConnectionId Id,
    long Revision,
    string Name,
    NetworkConnectionKind Kind,
    string Summary)
{
    public string KindLabel => NetworkConnectionPresentation.KindLabel(Kind);
}

/// <summary>
/// Owns the reusable connection profiles and the application default policy. Runtime
/// connection state belongs to the workspace network coordinator, not this editor.
/// </summary>
public sealed class NetworkSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly ISecretVault _secretVault;
    private readonly Dictionary<NetworkCredentialTarget, PendingNetworkCredential>
        _pendingCredentials = [];
    private NetworkConnectionProfileEditorViewModel? _profileEditor;
    private NetworkPolicyEditorViewModel _policy;
    private long? _policyRevision;
    private string _applicationSettingsName;
    private string _profileCatalogIdentity = string.Empty;
    private string? _operationStatus;
    private bool _hasError;
    private bool _disposed;

    public NetworkSettingsViewModel(
        IDefinitionCatalog catalog,
        ISecretVault secretVault)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _policy = new([], NetworkPolicy.Direct);
        _applicationSettingsName = ApplicationNetworkSettings.Default.Name;
        ApplyCatalog(_catalog.Snapshot);
    }

    public ObservableCollection<NetworkConnectionProfileItemViewModel> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => !HasProfiles;

    public NetworkConnectionProfileEditorViewModel? ProfileEditor
    {
        get => _profileEditor;
        private set
        {
            if (SetProperty(ref _profileEditor, value))
            {
                OnPropertyChanged(nameof(HasProfileEditor));
            }
        }
    }

    public bool HasProfileEditor => ProfileEditor is not null;

    public bool CanStoreCredentials =>
        _secretVault.Availability.CanPersist
        && (_secretVault.Availability.Capabilities & SecretVaultCapabilities.Create) != 0;

    public string CredentialVaultStatus => _secretVault.Availability.Message;

    public NetworkPolicyEditorViewModel Policy
    {
        get => _policy;
        private set
        {
            if (ReferenceEquals(_policy, value))
            {
                return;
            }

            var previous = _policy;
            if (SetProperty(ref _policy, value))
            {
                previous.Dispose();
            }
        }
    }

    public string? OperationStatus
    {
        get => _operationStatus;
        private set
        {
            if (SetProperty(ref _operationStatus, value))
            {
                OnPropertyChanged(nameof(HasOperationStatus));
            }
        }
    }

    public bool HasOperationStatus => OperationStatus is not null;

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public void BeginCreateProfile()
    {
        ThrowIfDisposed();
        if (ProfileEditor is not null)
        {
            Fail("Save or cancel the open network connection before creating another one.");
            return;
        }

        ProfileEditor = new();
        ClearStatus();
    }

    public void BeginEditProfile(NetworkConnectionProfileItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (ProfileEditor is not null)
        {
            Fail("Save or cancel the open network connection before editing another one.");
            return;
        }

        var stored = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(candidate => candidate.Value.Id == item.Id);
        if (stored is null)
        {
            Fail("That network connection no longer exists.");
            return;
        }

        ProfileEditor = new(stored.Value, stored.Revision);
        ClearStatus();
    }

    public async ValueTask CancelProfileEditAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var cleanupFailures = await DiscardPendingCredentialsAsync(cancellationToken);
        ProfileEditor = null;
        if (cleanupFailures == 0)
        {
            ClearStatus();
        }
        else
        {
            Warn("The connection draft was discarded, but an unused credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }
    }

    public async ValueTask<bool> SaveProfileAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ProfileEditor is null)
        {
            return Fail("Open a network connection before saving it.");
        }

        NetworkConnectionProfileSaveRequest request;
        try
        {
            request = ProfileEditor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }

        var original = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(item => item.Value.Id == request.Profile.Id)?.Value;
        DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>> result;
        try
        {
            var credentialError = await StorePendingCredentialsAsync(
                request.Profile,
                cancellationToken);
            if (credentialError is not null)
            {
                return Fail(credentialError);
            }

            result = await _catalog.SaveNetworkConnectionAsync(
                request.Profile,
                request.ExpectedRevision,
                cancellationToken);
        }
        catch
        {
            _ = await ResetStoredPendingCredentialsAsync(CancellationToken.None);
            throw;
        }

        if (!result.IsSuccess)
        {
            var cleanupFailures = await ResetStoredPendingCredentialsAsync(
                CancellationToken.None);
            var message = result.Error?.Message ?? "The network connection could not be saved.";
            return Fail(cleanupFailures == 0
                ? message
                : message + " An unused credential could not be removed from the operating-system vault.");
        }

        var finalReferences = CredentialReferences(request.Profile.Configuration);
        var detachedReferences = original is null
            ? []
            : CredentialReferences(original.Configuration)
                .Except(finalReferences)
                .ToArray();
        var unusedPendingReferences = _pendingCredentials.Values
            .Where(item => item.IsStored && !finalReferences.Contains(item.Reference))
            .Select(item => item.Reference);
        var cleanupFailuresAfterSave = await DeleteCredentialsAsync(
            detachedReferences.Concat(unusedPendingReferences).Distinct(),
            request.Profile.Id,
            CancellationToken.None);
        DisposePendingCredentials();
        ProfileEditor = null;
        ApplyCatalog(_catalog.Snapshot);
        if (cleanupFailuresAfterSave == 0)
        {
            Succeed($"Saved {request.Profile.Name}.");
        }
        else
        {
            Warn($"Saved {request.Profile.Name}, but an old credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }

        return true;
    }

    public async ValueTask<bool> StoreCredentialAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ProfileEditor is not { Credential.SelectedTarget: { } target } editor
            || !editor.Credential.CanStore)
        {
            return Fail("Choose a credential type, label it, and enter its value.");
        }

        if (!CanStoreCredentials)
        {
            return Fail(CredentialVaultStatus);
        }

        if (_pendingCredentials.Remove(target.Target, out var previous))
        {
            if (previous.IsStored
                && await DeleteCredentialAsync(
                    previous.Reference,
                    editor.Id,
                    cancellationToken) is false)
            {
                _pendingCredentials.Add(target.Target, previous);
                return Fail("The previous credential could not be replaced because it could not be removed from the operating-system vault.");
            }

            previous.Dispose();
        }

        var reference = SecretRef.New();
        var material = SecretMaterial.TakeOwnership(
            Encoding.UTF8.GetBytes(editor.Credential.Value));
        _pendingCredentials.Add(
            target.Target,
            new PendingNetworkCredential(
                reference,
                editor.Credential.Label,
                target.Kind,
                material));
        editor.ApplyCredential(target.Target, reference);
        editor.Credential.ClearValue();
        Succeed($"{target.DisplayName} is ready. Save the connection to store it in the credential vault.");
        return true;
    }

    public async ValueTask<bool> DeleteProfileAsync(
        NetworkConnectionProfileItemViewModel item,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        var stored = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(candidate => candidate.Value.Id == item.Id);
        if (stored is null)
        {
            return Fail("That network connection no longer exists.");
        }

        var result = await _catalog.DeleteAsync(
            new DefinitionKey(NetworkConnectionProfile.Kind, item.Id.Value),
            item.Revision,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return Fail(result.Error?.Message ?? "The network connection could not be deleted.");
        }

        var cleanupFailures = await DeleteCredentialsAsync(
            CredentialReferences(stored.Value.Configuration),
            item.Id,
            CancellationToken.None);
        if (ProfileEditor?.Id == item.Id)
        {
            cleanupFailures += await DiscardPendingCredentialsAsync(CancellationToken.None);
            ProfileEditor = null;
        }

        ApplyCatalog(_catalog.Snapshot);
        if (cleanupFailures == 0)
        {
            Succeed($"Deleted {item.Name}.");
        }
        else
        {
            Warn($"Deleted {item.Name}, but a credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }

        return true;
    }

    public async ValueTask<bool> SavePolicyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        NetworkPolicy policy;
        try
        {
            policy = Policy.CreatePolicy();
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }

        var settings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            _applicationSettingsName,
            policy);
        var result = await _catalog.SaveApplicationNetworkSettingsAsync(
            settings,
            _policyRevision,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return Fail(result.Error?.Message ?? "Application networking could not be saved.");
        }

        ApplyCatalog(_catalog.Snapshot);
        Succeed("Saved application networking.");
        return true;
    }

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        var storedSettings = snapshot.ApplicationNetworkSettings.SingleOrDefault(item =>
            item.Value.Id == ApplicationNetworkSettings.DefaultId);
        var settings = storedSettings?.Value ?? ApplicationNetworkSettings.Default;
        var profileIdentity = string.Join(
            '|',
            snapshot.NetworkConnections
                .OrderBy(item => item.Value.Id.Value, StringComparer.Ordinal)
                .Select(item => $"{item.Value.Id.Value}:{item.Revision}"));
        var policyRevisionChanged = _policyRevision != storedSettings?.Revision;
        var profilesChanged = !string.Equals(
            _profileCatalogIdentity,
            profileIdentity,
            StringComparison.Ordinal);

        ReplaceProfiles(snapshot.NetworkConnections);
        if (policyRevisionChanged || profilesChanged)
        {
            var preserveDraft = !policyRevisionChanged && Policy.IsDirty && Policy.IsValid;
            var policy = preserveDraft ? Policy.CreatePolicy() : settings.Policy;
            Policy = new(
                [.. snapshot.NetworkConnections.Select(item => item.Value)],
                policy,
                isDirty: preserveDraft);
        }

        _policyRevision = storedSettings?.Revision;
        _applicationSettingsName = settings.Name;
        _profileCatalogIdentity = profileIdentity;
    }

    public void ClearStatus()
    {
        HasError = false;
        OperationStatus = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposePendingCredentials();
        ProfileEditor = null;
        Policy.Dispose();
    }

    private void ReplaceProfiles(
        IReadOnlyList<StoredDefinition<NetworkConnectionProfile>> profiles)
    {
        var projected = profiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new NetworkConnectionProfileItemViewModel(
                item.Value.Id,
                item.Revision,
                item.Value.Name,
                item.Value.ConnectionKind,
                NetworkConnectionPresentation.Summary(item.Value.Configuration)))
            .ToArray();
        if (Profiles.SequenceEqual(projected))
        {
            return;
        }

        Profiles.Clear();
        foreach (var item in projected)
        {
            Profiles.Add(item);
        }

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private bool Fail(string message)
    {
        HasError = true;
        OperationStatus = message;
        return false;
    }

    private void Succeed(string message)
    {
        HasError = false;
        OperationStatus = message;
    }

    private void Warn(string message)
    {
        HasError = true;
        OperationStatus = message;
    }

    private async ValueTask<string?> StorePendingCredentialsAsync(
        NetworkConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var references = CredentialReferences(profile.Configuration);
        foreach (var pending in _pendingCredentials.Values.Where(item =>
                     references.Contains(item.Reference) && !item.IsStored))
        {
            using var material = pending.Material.Clone();
            var result = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    pending.Reference,
                    pending.Label,
                    pending.Kind,
                    NetworkCredentialScope(profile.Id),
                    new SecretUsePurpose(
                        SecretUseKind.NetworkConnectionAuthentication,
                        profile.Id.Value)),
                material,
                cancellationToken);
            if (result is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                _ = await ResetStoredPendingCredentialsAsync(CancellationToken.None);
                return failure.Error.Message;
            }

            pending.IsStored = true;
        }

        return null;
    }

    private async ValueTask<int> ResetStoredPendingCredentialsAsync(
        CancellationToken cancellationToken)
    {
        if (ProfileEditor is not { } editor)
        {
            return 0;
        }

        var failures = 0;
        foreach (var pending in _pendingCredentials.Values.Where(item => item.IsStored))
        {
            if (await DeleteCredentialAsync(
                pending.Reference,
                editor.Id,
                cancellationToken))
            {
                pending.IsStored = false;
            }
            else
            {
                failures++;
            }
        }

        return failures;
    }

    private async ValueTask<int> DiscardPendingCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var failures = await ResetStoredPendingCredentialsAsync(cancellationToken);
        DisposePendingCredentials();
        return failures;
    }

    private async ValueTask<int> DeleteCredentialsAsync(
        IEnumerable<SecretRef> references,
        NetworkConnectionId ownerId,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var reference in references)
        {
            if (!await DeleteCredentialAsync(reference, ownerId, cancellationToken))
            {
                failures++;
            }
        }

        return failures;
    }

    private async ValueTask<bool> DeleteCredentialAsync(
        SecretRef reference,
        NetworkConnectionId ownerId,
        CancellationToken cancellationToken)
    {
        var result = await _secretVault.DeleteAsync(
            new DeleteSecretRequest(
                reference,
                NetworkCredentialScope(ownerId),
                new SecretUsePurpose(SecretUseKind.UserManagement, ownerId.Value)),
            cancellationToken);
        return result is SecretVaultResult<Unit>.Success;
    }

    private void DisposePendingCredentials()
    {
        foreach (var pending in _pendingCredentials.Values)
        {
            pending.Dispose();
        }

        _pendingCredentials.Clear();
    }

    private static HashSet<SecretRef> CredentialReferences(
        NetworkConnectionConfiguration configuration) => configuration switch
        {
            NetworkConnectionConfiguration.Proxy proxy => Set(proxy.PasswordSecret),
            NetworkConnectionConfiguration.WireGuard wireGuard =>
                [wireGuard.ConfigurationSecret],
            NetworkConnectionConfiguration.OpenVpn openVpn =>
                [openVpn.ConfigurationSecret],
            NetworkConnectionConfiguration.AnyConnect anyConnect =>
                Set(anyConnect.PasswordSecret, anyConnect.ClientCertificateSecret),
            NetworkConnectionConfiguration.Tailscale tailscale => Set(tailscale.AuthKeySecret),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };

    private static HashSet<SecretRef> Set(params SecretRef?[] references) =>
        [.. references.OfType<SecretRef>()];

    private static SecretScope NetworkCredentialScope(NetworkConnectionId ownerId) =>
        new(SecretScopeKind.NetworkConnection, ownerId.Value);

    private sealed class PendingNetworkCredential(
        SecretRef reference,
        string label,
        SecretKind kind,
        SecretMaterial material) : IDisposable
    {
        public SecretRef Reference { get; } = reference;

        public string Label { get; } = label;

        public SecretKind Kind { get; } = kind;

        public SecretMaterial Material { get; } = material;

        public bool IsStored { get; set; }

        public void Dispose() => Material.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
