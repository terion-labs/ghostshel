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
        }

        _observedPanel = Panel;
        if (_observedPanel is not null)
        {
            _observedPanel.PropertyChanged += OnPanelPropertyChanged;
        }

        SyncConnectionStringBox();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(DatabaseRuntimePanelViewModel.MaskedConnectionString))
        {
            SyncConnectionStringBox();
        }
    }

    private void SyncConnectionStringBox()
    {
        if (!ConnectionStringBox.IsFocused)
        {
            ConnectionStringBox.Text = Panel?.MaskedConnectionString ?? string.Empty;
        }
    }

    private void OnConnectionStringGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
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
        if (Panel is { } panel && ConnectionStringBox.IsFocused)
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
            panel.ParseConnectionDetails());
        var details = await dialog.ShowDialog<GhostShell.Application.DatabaseConnectionDetails?>(owner);
        if (details is not null)
        {
            await panel.ApplyConnectionDetailsAsync(details);
        }
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
