using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// What saving the database form means: the shell persists the profile (and
/// optionally moves the password into the OS vault) through the catalog's
/// database save path rather than a plain definition write.
/// </summary>
public sealed record DatabaseConnectionSaveRequest(
    DatabaseConnectionProfileId? ExistingId,
    string Name,
    string DriverId,
    DatabaseConnectionDetails Details,
    bool StorePassword,
    ConnectionId? TunnelConnectionId);

public sealed record DatabaseTunnelOption(ConnectionId? Id, string Name)
{
    public string DisplayName => Id is null ? "No tunnel" : Name;
}

/// <summary>
/// Structural editor for a saved database connection: driver, endpoint or file,
/// credentials, and an optional SSH tunnel. The stored connection string never
/// carries the password; the field here feeds the OS vault when requested.
/// </summary>
public sealed class DatabaseConnectionEditorViewModel : ObservableObject
{
    private readonly IDatabasePanelClient _client;
    private readonly DatabaseConnectionProfileId? _existingId;
    private DatabaseDriverDescriptor _selectedDriver;
    private DatabaseTunnelOption _selectedTunnel;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private string _port = string.Empty;
    private string _databaseName = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _filePath = string.Empty;
    private string _options = string.Empty;
    private bool _storePassword;

    public DatabaseConnectionEditorViewModel(
        IDatabasePanelClient client,
        IReadOnlyList<ConnectionProfile> connections,
        DatabaseConnectionProfile? existing = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(connections);
        Drivers = client.Drivers;
        _selectedDriver = Drivers[0];
        TunnelOptions = BuildTunnelOptions(connections, existing);
        _selectedTunnel = TunnelOptions[0];

        if (existing is null)
        {
            return;
        }

        _existingId = existing.Id;
        _name = existing.Name;
        _selectedDriver = Drivers.FirstOrDefault(item => item.Id == existing.DriverId)
            ?? Drivers[0];
        _selectedTunnel = TunnelOptions.FirstOrDefault(item =>
                item.Id == existing.TunnelConnectionId)
            ?? TunnelOptions[0];
        HasStoredPassword = existing.PasswordSecret is not null;
        var details = client.ParseConnectionDetails(
            existing.DriverId,
            existing.ConnectionString);
        _host = details.Host ?? string.Empty;
        _port = details.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _databaseName = details.Database ?? string.Empty;
        _username = details.Username ?? string.Empty;
        _filePath = details.FilePath ?? string.Empty;
        _options = details.Options ?? string.Empty;
    }

    public bool IsEditing => _existingId is not null;

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    public IReadOnlyList<DatabaseTunnelOption> TunnelOptions { get; }

    /// <summary>A stored keychain password exists and is kept unless replaced.</summary>
    public bool HasStoredPassword { get; }

    public DatabaseDriverDescriptor SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (SetProperty(ref _selectedDriver, value))
            {
                OnPropertyChanged(nameof(IsFileBased));
                OnPropertyChanged(nameof(IsServerBased));
            }
        }
    }

    public DatabaseTunnelOption SelectedTunnel
    {
        get => _selectedTunnel;
        set => SetProperty(ref _selectedTunnel, value);
    }

    public bool IsFileBased => SelectedDriver.IsFileBased;

    public bool IsServerBased => !SelectedDriver.IsFileBased;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string DatabaseName
    {
        get => _databaseName;
        set => SetProperty(ref _databaseName, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string Options
    {
        get => _options;
        set => SetProperty(ref _options, value);
    }

    public bool StorePassword
    {
        get => _storePassword;
        set => SetProperty(ref _storePassword, value);
    }

    public DatabaseConnectionSaveRequest CreateSaveRequest()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Connection name is required.");
        }

        DatabaseConnectionDetails details;
        if (IsFileBased)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                throw new ArgumentException("Database file path is required.");
            }

            details = new DatabaseConnectionDetails(
                FilePath: FilePath.Trim(),
                Options: Optional(Options));
        }
        else
        {
            int? port = null;
            if (!string.IsNullOrWhiteSpace(Port))
            {
                if (!int.TryParse(
                        Port.Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                    || parsed is < 1 or > 65_535)
                {
                    throw new ArgumentException("Port must be a number between 1 and 65535.");
                }

                port = parsed;
            }

            details = new DatabaseConnectionDetails(
                Optional(Host),
                port,
                Optional(DatabaseName),
                Optional(Username),
                string.IsNullOrEmpty(Password) ? null : Password,
                Options: Optional(Options));
        }

        // The connection string must round-trip through the driver before the
        // request leaves the dialog, so malformed options fail here as a
        // validation error rather than during the save.
        _ = _client.BuildConnectionString(SelectedDriver.Id, details);
        return new DatabaseConnectionSaveRequest(
            _existingId,
            Name.Trim(),
            SelectedDriver.Id,
            details,
            StorePassword,
            SelectedTunnel.Id);
    }

    private static IReadOnlyList<DatabaseTunnelOption> BuildTunnelOptions(
        IReadOnlyList<ConnectionProfile> connections,
        DatabaseConnectionProfile? existing)
    {
        var options = new List<DatabaseTunnelOption>
        {
            new(null, "No tunnel"),
        };
        options.AddRange(connections
            .Where(item => item.Endpoint is ConnectionEndpoint.Ssh)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DatabaseTunnelOption(item.Id, item.Name)));
        if (existing?.TunnelConnectionId is { } tunnelId
            && options.All(item => item.Id != tunnelId))
        {
            options.Add(new DatabaseTunnelOption(tunnelId, $"Missing · {tunnelId.Value}"));
        }

        return options.AsReadOnly();
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
