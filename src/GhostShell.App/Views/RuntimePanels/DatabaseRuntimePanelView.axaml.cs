using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    private void OnConnectionStringGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { IsSavedConnection: false } panel)
        {
            ConnectionStringBox.Text = panel.ConnectionString;
        }
    }

    private void OnConnectionStringLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CommitConnectionString();
        SyncConnectionStringBox();
    }

    private void CommitConnectionString()
    {
        if (Panel is { IsSavedConnection: false } panel && ConnectionStringBox.IsFocused)
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

    private void OnSplitLeftRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.LeftRight);
    }

    private void OnSplitTopBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.TopBottom);
    }

    private void OnConnectionStringKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Panel is { } panel)
        {
            e.Handled = true;
            CommitConnectionString();
            _ = panel.ConnectAsync();
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && (e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            && Panel is { } panel)
        {
            e.Handled = true;
            _ = panel.RunQueryAsync();
        }
    }

    private bool _resizingInspector;
    private double _resizeOriginX;
    private double _resizeOriginWidth;

    private void OnInspectorResizePressed(object? sender, PointerPressedEventArgs e)
    {
        _resizingInspector = true;
        _resizeOriginX = e.GetPosition(this).X;
        _resizeOriginWidth = InspectorColumn.Width;
        e.Pointer.Capture(InspectorResizeHandle);
        e.Handled = true;
    }

    private void OnInspectorResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingInspector)
        {
            var delta = _resizeOriginX - e.GetPosition(this).X;
            InspectorColumn.Width = Math.Clamp(_resizeOriginWidth + delta, 200, 560);
        }
    }

    private void OnInspectorResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _resizingInspector = false;
        e.Pointer.Capture(null);
    }

    private async void OnCopyRowJsonClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CopySelectedRowAsync(static (panel, row) => panel.BuildRowJson(row));
    }

    private async void OnCopyRowCsvClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CopySelectedRowAsync(static (panel, row) => panel.BuildRowCsv(row));
    }

    private async void OnCopyRowSqlClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CopySelectedRowAsync(static (panel, row) => panel.BuildRowSqlInsert(row));
    }

    private async System.Threading.Tasks.Task CopySelectedRowAsync(
        Func<DatabaseRuntimePanelViewModel, DatabaseResultRowViewModel, string> build)
    {
        if (Panel is { SelectedRow: { } row } panel
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(build(panel, row));
        }
    }

    private void OnDismissInspectorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.SelectRow(null);
    }

    private void OnResultRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseResultRowViewModel row }
            && Panel is { } panel)
        {
            panel.SelectRow(row);
        }
    }

    private void OnTableClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseTableItemViewModel table }
            && Panel is { } panel)
        {
            _ = panel.PreviewTableAsync(table);
        }
    }
}
