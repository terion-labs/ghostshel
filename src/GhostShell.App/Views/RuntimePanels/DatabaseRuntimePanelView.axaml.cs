using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class DatabaseRuntimePanelView : UserControl
{
    private DatabaseRuntimePanelViewModel? _observedPanel;

    public DatabaseRuntimePanelView()
    {
        InitializeComponent();
        // The address bar shows a masked presentation while idle and the raw
        // string only during editing, so its text is managed here rather than
        // bound: a two-way binding would write the mask back over the value.
        DataContextChanged += (_, _) => ObservePanel();
    }

    private void ObservePanel()
    {
        if (_observedPanel is not null)
        {
            _observedPanel.PropertyChanged -= OnPanelPropertyChanged;
            _observedPanel.PasswordRequested -= OnPasswordRequested;
        }

        _observedPanel = Panel;
        if (_observedPanel is not null)
        {
            _observedPanel.PropertyChanged += OnPanelPropertyChanged;
            _observedPanel.PasswordRequested += OnPasswordRequested;
        }

        SyncConnectionStringBox();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(DatabaseRuntimePanelViewModel.MaskedConnectionString)
            or nameof(DatabaseRuntimePanelViewModel.AddressBarText)
            or nameof(DatabaseRuntimePanelViewModel.IsSavedConnection))
        {
            SyncConnectionStringBox();
        }
    }

    private void SyncConnectionStringBox()
    {
        // A saved connection shows its name and is not editable in place; the
        // details dialog is the editor.
        ConnectionStringBox.IsReadOnly = Panel?.IsSavedConnection == true;
        if (!ConnectionStringBox.IsFocused || ConnectionStringBox.IsReadOnly)
        {
            ConnectionStringBox.Text = Panel?.AddressBarText ?? string.Empty;
        }
    }

    private async void OnPasswordRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { } panel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new DatabasePasswordPromptDialog(
            panel.SavedConnectionName ?? "Database");
        var password = await dialog.ShowDialog<string?>(owner);
        if (password is not null)
        {
            panel.SetSessionPassword(password);
            await panel.ConnectAsync();
        }
    }

    /// <summary>
    /// True while the box holds the raw string the user is editing. Text is
    /// committed on every change during editing: the Connect button runs a
    /// bound command that never passes through this view, and a focus-loss
    /// commit fires after focus is already gone, so an edge-triggered commit
    /// loses the typed string exactly when it is needed.
    /// </summary>
    private bool _editingConnectionString;

    private void OnConnectionStringGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { IsSavedConnection: false } panel)
        {
            ConnectionStringBox.Text = panel.ConnectionString;
            _editingConnectionString = true;
        }
    }

    private void OnConnectionStringLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CommitConnectionString();
        _editingConnectionString = false;
        SyncConnectionStringBox();
    }

    private void OnConnectionStringTextChanged(object? sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_editingConnectionString)
        {
            CommitConnectionString();
        }
    }

    private void CommitConnectionString()
    {
        if (Panel is { IsSavedConnection: false } panel
            && (_editingConnectionString || ConnectionStringBox.IsFocused))
        {
            panel.ConnectionString = ConnectionStringBox.Text ?? string.Empty;
        }
    }

    private async void OnConnectionDetailsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { } panel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        CommitConnectionString();
        var dialog = new DatabaseConnectionDetailsDialog(
            panel.SelectedDriver.DisplayName,
            panel.SelectedDriver.IsFileBased,
            panel.ParseConnectionDetails(),
            panel.SavedConnectionName);
        var result = await dialog.ShowDialog<DatabaseConnectionDialogResult?>(owner);
        if (result is null)
        {
            return;
        }

        if (result.SaveName is { } saveName
            && owner.DataContext is MainWindowViewModel shell)
        {
            var profile = await shell.SaveDatabaseConnectionAsync(
                panel.SavedConnectionId,
                saveName,
                panel.SelectedDriver.Id,
                result.Details,
                result.StorePassword,
                panel.TunnelConnectionId);
            if (profile is not null)
            {
                panel.ApplySavedConnection(profile, result.Details.Password);
                return;
            }
        }

        await panel.ApplyConnectionDetailsAsync(result.Details);
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private DatabaseRuntimePanelViewModel? Panel => DataContext as DatabaseRuntimePanelViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(sender, e);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void OnConnectionStringKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Panel is { } panel)
        {
            e.Handled = true;
            CommitConnectionString();
            _ = panel.ConnectAsync();
        }
    }

}
