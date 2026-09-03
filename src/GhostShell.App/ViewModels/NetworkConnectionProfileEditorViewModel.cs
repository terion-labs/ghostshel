using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record NetworkConnectionKindOption(
    NetworkConnectionKind Kind,
    string DisplayName,
    string Description);

public sealed record NetworkProxyProtocolOption(
    NetworkProxyProtocol Protocol,
    string DisplayName);

public sealed record NetworkConnectionProfileSaveRequest(
    NetworkConnectionProfile Profile,
    long? ExpectedRevision);

/// <summary>
/// Owns one network connection draft. Credential fields contain only vault references;
/// secret material never enters the durable profile.
/// </summary>
public sealed class NetworkConnectionProfileEditorViewModel : ObservableObject
{
    private readonly NetworkConnectionId _id;
    private readonly long? _expectedRevision;
    private string _name;
    private NetworkConnectionKindOption _selectedKind;
    private NetworkProxyProtocolOption _selectedProxyProtocol;
    private string _host = string.Empty;
    private string _port = string.Empty;
    private string _username = string.Empty;
    private string _passwordSecretReference = string.Empty;
    private string _configurationSecretReference = string.Empty;
    private string _gateway = string.Empty;
    private string _authenticationGroup = string.Empty;
    private string _clientCertificateSecretReference = string.Empty;
    private string _exitNode = string.Empty;
    private string _controlServer = string.Empty;
    private string _authKeySecretReference = string.Empty;
    private bool _isDirty;

    public NetworkConnectionProfileEditorViewModel()
        : this(null, null)
    {
    }

    public NetworkConnectionProfileEditorViewModel(
        NetworkConnectionProfile? profile,
        long? expectedRevision)
    {
        KindOptions =
        [
            new(NetworkConnectionKind.Proxy, "Proxy", "SOCKS5, HTTP, or HTTPS proxy."),
            new(NetworkConnectionKind.WireGuard, "WireGuard", "WireGuard client configuration stored in the credential vault."),
            new(NetworkConnectionKind.OpenVpn, "OpenVPN", "OpenVPN client profile stored in the credential vault."),
            new(NetworkConnectionKind.AnyConnect, "AnyConnect", "Cisco AnyConnect-compatible gateway through OpenConnect."),
            new(NetworkConnectionKind.Tailscale, "Tailscale", "Tailscale exit node for workspace traffic."),
        ];
        ProxyProtocolOptions =
        [
            new(NetworkProxyProtocol.Socks5, "SOCKS5"),
            new(NetworkProxyProtocol.Http, "HTTP"),
            new(NetworkProxyProtocol.Https, "HTTPS"),
        ];

        _id = profile?.Id ?? NetworkConnectionId.New();
        _expectedRevision = expectedRevision;
        _name = profile?.Name ?? "New network connection";
        _selectedKind = KindOptions.Single(option =>
            option.Kind == (profile?.ConnectionKind ?? NetworkConnectionKind.Proxy));
        _selectedProxyProtocol = ProxyProtocolOptions[0];
        Credential = new();
        Credential.SetConnectionKind(_selectedKind.Kind);
        if (profile is not null)
        {
            Restore(profile.Configuration);
        }
    }

    public IReadOnlyList<NetworkConnectionKindOption> KindOptions { get; }

    public IReadOnlyList<NetworkProxyProtocolOption> ProxyProtocolOptions { get; }

    public NetworkCredentialDraftViewModel Credential { get; }

    public NetworkConnectionId Id => _id;

    public long? ExpectedRevision => _expectedRevision;

    public bool IsNew => ExpectedRevision is null;

    public string Name
    {
        get => _name;
        set => Change(ref _name, value ?? string.Empty);
    }

    public NetworkConnectionKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedKind, value))
            {
                Credential.SetConnectionKind(value.Kind);
                PublishKind();
                Changed();
            }
        }
    }

    public NetworkProxyProtocolOption SelectedProxyProtocol
    {
        get => _selectedProxyProtocol;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedProxyProtocol, value))
            {
                Changed();
            }
        }
    }

    public string Host
    {
        get => _host;
        set => Change(ref _host, value ?? string.Empty);
    }

    public string Port
    {
        get => _port;
        set => Change(ref _port, value ?? string.Empty);
    }

    public string Username
    {
        get => _username;
        set => Change(ref _username, value ?? string.Empty);
    }

    public string PasswordSecretReference
    {
        get => _passwordSecretReference;
        set => Change(ref _passwordSecretReference, value ?? string.Empty);
    }

    public string ConfigurationSecretReference
    {
        get => _configurationSecretReference;
        set => Change(ref _configurationSecretReference, value ?? string.Empty);
    }

    public string Gateway
    {
        get => _gateway;
        set => Change(ref _gateway, value ?? string.Empty);
    }

    public string AuthenticationGroup
    {
        get => _authenticationGroup;
        set => Change(ref _authenticationGroup, value ?? string.Empty);
    }

    public string ClientCertificateSecretReference
    {
        get => _clientCertificateSecretReference;
        set => Change(ref _clientCertificateSecretReference, value ?? string.Empty);
    }

    public string ExitNode
    {
        get => _exitNode;
        set => Change(ref _exitNode, value ?? string.Empty);
    }

    public string ControlServer
    {
        get => _controlServer;
        set => Change(ref _controlServer, value ?? string.Empty);
    }

    public string AuthKeySecretReference
    {
        get => _authKeySecretReference;
        set => Change(ref _authKeySecretReference, value ?? string.Empty);
    }

    public bool IsProxy => SelectedKind.Kind == NetworkConnectionKind.Proxy;

    public bool IsWireGuard => SelectedKind.Kind == NetworkConnectionKind.WireGuard;

    public bool IsOpenVpn => SelectedKind.Kind == NetworkConnectionKind.OpenVpn;

    public bool IsConfigurationFileVpn => IsWireGuard || IsOpenVpn;

    public bool IsAnyConnect => SelectedKind.Kind == NetworkConnectionKind.AnyConnect;

    public bool IsTailscale => SelectedKind.Kind == NetworkConnectionKind.Tailscale;

    public string ConfigurationSecretLabel => IsWireGuard
        ? "WireGuard configuration credential"
        : "OpenVPN profile credential";

    public string PasswordCredentialState => CredentialState(PasswordSecretReference);

    public string ConfigurationCredentialState => CredentialState(ConfigurationSecretReference);

    public string ClientCertificateCredentialState =>
        CredentialState(ClientCertificateSecretReference);

    public string AuthKeyCredentialState => CredentialState(AuthKeySecretReference);

    public bool IsDirty => _isDirty;

    public bool IsValid => TryBuild(out _, out _);

    public bool CanSave => IsDirty && IsValid;

    public string StatusLabel => IsValid ? "Valid" : "Needs attention";

    public bool HasValidationError => !IsValid;

    public string ValidationMessage => TryBuild(out _, out var error)
        ? "Connection configuration is valid."
        : error;

    public NetworkConnectionProfileSaveRequest CreateSaveRequest()
    {
        if (!TryBuild(out var profile, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return new(profile!, ExpectedRevision);
    }

    public void ApplyCredential(NetworkCredentialTarget target, SecretRef reference)
    {
        switch (target)
        {
            case NetworkCredentialTarget.ProxyPassword:
            case NetworkCredentialTarget.AnyConnectPassword:
                PasswordSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.WireGuardConfiguration:
            case NetworkCredentialTarget.OpenVpnConfiguration:
                ConfigurationSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.AnyConnectClientCertificate:
                ClientCertificateSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.TailscaleAuthKey:
                AuthKeySecretReference = reference.Value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private bool TryBuild(
        out NetworkConnectionProfile? profile,
        out string error)
    {
        try
        {
            profile = new(
                Id,
                NetworkConnectionProfile.CurrentSchemaVersion,
                Name,
                BuildConfiguration());
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            profile = null;
            error = FriendlyValidationMessage(exception);
            return false;
        }
    }

    private NetworkConnectionConfiguration BuildConfiguration() => SelectedKind.Kind switch
    {
        NetworkConnectionKind.Proxy => new NetworkConnectionConfiguration.Proxy(
            SelectedProxyProtocol.Protocol,
            Host,
            RequirePort(),
            Optional(Username),
            OptionalSecret(PasswordSecretReference)),
        NetworkConnectionKind.WireGuard => new NetworkConnectionConfiguration.WireGuard(
            RequiredSecret(ConfigurationSecretReference, "WireGuard configuration credential")),
        NetworkConnectionKind.OpenVpn => new NetworkConnectionConfiguration.OpenVpn(
            RequiredSecret(ConfigurationSecretReference, "OpenVPN profile credential")),
        NetworkConnectionKind.AnyConnect => new NetworkConnectionConfiguration.AnyConnect(
            new Uri(Gateway, UriKind.RelativeOrAbsolute),
            Optional(Username),
            OptionalSecret(PasswordSecretReference),
            Optional(AuthenticationGroup),
            OptionalSecret(ClientCertificateSecretReference)),
        NetworkConnectionKind.Tailscale => new NetworkConnectionConfiguration.Tailscale(
            ExitNode,
            OptionalUri(ControlServer),
            OptionalSecret(AuthKeySecretReference)),
        _ => throw new InvalidOperationException("Choose a supported network connection type."),
    };

    private int RequirePort()
    {
        if (!int.TryParse(
            Port,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port))
        {
            throw new ArgumentException("Enter a proxy port between 1 and 65535.");
        }

        return port;
    }

    private void Restore(NetworkConnectionConfiguration configuration)
    {
        switch (configuration)
        {
            case NetworkConnectionConfiguration.Proxy proxy:
                _selectedProxyProtocol = ProxyProtocolOptions.Single(option =>
                    option.Protocol == proxy.Protocol);
                _host = proxy.Host;
                _port = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _username = proxy.Username ?? string.Empty;
                _passwordSecretReference = proxy.PasswordSecret?.Value ?? string.Empty;
                break;
            case NetworkConnectionConfiguration.WireGuard wireGuard:
                _configurationSecretReference = wireGuard.ConfigurationSecret.Value;
                break;
            case NetworkConnectionConfiguration.OpenVpn openVpn:
                _configurationSecretReference = openVpn.ConfigurationSecret.Value;
                break;
            case NetworkConnectionConfiguration.AnyConnect anyConnect:
                _gateway = anyConnect.Gateway.AbsoluteUri;
                _username = anyConnect.Username ?? string.Empty;
                _passwordSecretReference = anyConnect.PasswordSecret?.Value ?? string.Empty;
                _authenticationGroup = anyConnect.AuthenticationGroup ?? string.Empty;
                _clientCertificateSecretReference =
                    anyConnect.ClientCertificateSecret?.Value ?? string.Empty;
                break;
            case NetworkConnectionConfiguration.Tailscale tailscale:
                _exitNode = tailscale.ExitNode;
                _controlServer = tailscale.ControlServer?.AbsoluteUri ?? string.Empty;
                _authKeySecretReference = tailscale.AuthKeySecret?.Value ?? string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private void Change(ref string field, string value)
    {
        if (SetProperty(ref field, value))
        {
            Changed();
        }
    }

    private void Changed()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(PasswordCredentialState));
        OnPropertyChanged(nameof(ConfigurationCredentialState));
        OnPropertyChanged(nameof(ClientCertificateCredentialState));
        OnPropertyChanged(nameof(AuthKeyCredentialState));
    }

    private void PublishKind()
    {
        OnPropertyChanged(nameof(IsProxy));
        OnPropertyChanged(nameof(IsWireGuard));
        OnPropertyChanged(nameof(IsOpenVpn));
        OnPropertyChanged(nameof(IsConfigurationFileVpn));
        OnPropertyChanged(nameof(IsAnyConnect));
        OnPropertyChanged(nameof(IsTailscale));
        OnPropertyChanged(nameof(ConfigurationSecretLabel));
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CredentialState(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Not configured" : "Configured";

    private static SecretRef? OptionalSecret(string value) =>
        Optional(value) is { } reference ? new SecretRef(reference) : null;

    private static SecretRef RequiredSecret(string value, string label) =>
        new(string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Enter the {label} reference.")
            : value.Trim());

    private static Uri? OptionalUri(string value) =>
        Optional(value) is { } uri ? new Uri(uri, UriKind.RelativeOrAbsolute) : null;

    private static string FriendlyValidationMessage(Exception exception)
    {
        var message = exception.Message;
        var parameterSuffix = message.IndexOf("\nParameter", StringComparison.Ordinal);
        if (parameterSuffix > 0)
        {
            message = message[..parameterSuffix];
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        return "Complete the required fields with a valid network configuration.";
    }
}
