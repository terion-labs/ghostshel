using GhostShell.Application;

namespace GhostShell.App.ViewModels;

/// <summary>
/// The application-security controls on the Security &amp; secrets page.
/// Flipping the encryption switch converts what is on disk right then; the
/// switch reports the true state afterwards, so a conversion that refuses
/// leaves the switch where the disk really is, with the reason underneath.
/// </summary>
public sealed class ApplicationSecurityEditorViewModel : ObservableObject
{
    private readonly IApplicationEncryption? _encryption;
    private bool _busy;
    private string? _statusDetail;

    public ApplicationSecurityEditorViewModel(IApplicationEncryption? encryption = null)
    {
        _encryption = encryption;
        if (_encryption is not null)
        {
            _encryption.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(IsEncryptionEnabled));
            };
        }
    }

    public bool IsEncryptionSupported => _encryption?.IsSupported ?? false;

    public string EncryptionAvailability => _encryption switch
    {
        null => "This build has no application-encryption support.",
        { IsSupported: false } unsupported => unsupported.UnsupportedReason
            ?? "Application encryption is unavailable.",
        _ => "Keys live in the OS keystore; nothing key-like is ever written beside the data.",
    };

    public bool IsEncryptionEnabled
    {
        get => _encryption?.IsEnabled ?? false;
        set
        {
            if (_encryption is null || _busy || value == _encryption.IsEnabled)
            {
                return;
            }

            _busy = true;
            StatusDetail = value
                ? "Encrypting the configuration database…"
                : "Decrypting the configuration database…";
            _ = ApplyAsync(value);
        }
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                OnPropertyChanged(nameof(HasStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => !string.IsNullOrEmpty(_statusDetail);

    private async Task ApplyAsync(bool enabled)
    {
        try
        {
            var refusal = await _encryption!.SetEnabledAsync(enabled, CancellationToken.None);
            StatusDetail = refusal;
        }
        catch (Exception exception)
        {
            StatusDetail = $"Changing application encryption failed: {exception.Message}";
        }
        finally
        {
            _busy = false;
            // The switch shows the disk's true state, whatever just happened.
            OnPropertyChanged(nameof(IsEncryptionEnabled));
        }
    }
}
