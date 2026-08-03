using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.Application;

namespace GhostShell.App.Views;

/// <summary>
/// The structural view of a database connection string: host, port, user,
/// password, database, and pass-through options — or just the file path for
/// file-based engines. Closes with the composed details, or null on cancel.
/// </summary>
public sealed partial class DatabaseConnectionDetailsDialog : Window
{
    public DatabaseConnectionDetailsDialog()
    {
        InitializeComponent();
    }

    public DatabaseConnectionDetailsDialog(
        string driverDisplayName,
        bool isFileBased,
        DatabaseConnectionDetails details)
        : this()
    {
        ArgumentNullException.ThrowIfNull(details);
        Title = $"{driverDisplayName} connection";
        DialogTitle.Text = $"{driverDisplayName} connection";
        FileSection.IsVisible = isFileBased;
        ServerSection.IsVisible = !isFileBased;
        FilePathInput.Text = details.FilePath ?? string.Empty;
        HostInput.Text = details.Host ?? string.Empty;
        PortInput.Text = details.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        UserInput.Text = details.Username ?? string.Empty;
        PasswordInput.Text = details.Password ?? string.Empty;
        DatabaseInput.Text = details.Database ?? string.Empty;
        OptionsInput.Text = details.Options ?? string.Empty;
    }

    public bool FocusInitialControl() => FileSection.IsVisible
        ? FilePathInput.Focus()
        : HostInput.Focus();

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(new DatabaseConnectionDetails(
            NullIfBlank(HostInput.Text),
            int.TryParse(PortInput.Text, out var port) ? port : null,
            NullIfBlank(DatabaseInput.Text),
            NullIfBlank(UserInput.Text),
            NullIfBlank(PasswordInput.Text),
            NullIfBlank(FilePathInput.Text),
            NullIfBlank(OptionsInput.Text)));
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
