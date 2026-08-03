using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.Components;

/// <summary>
/// The database workspace itself — objects list, statement editor, result grid,
/// and row inspector — without any panel chrome, so a docked database panel and
/// a file preview show the same viewer rather than two that drift apart.
/// </summary>
public sealed partial class DatabaseWorkspaceView : UserControl
{
    private bool _resizingInspector;
    private double _resizeOriginX;
    private double _resizeOriginWidth;

    public DatabaseWorkspaceView()
    {
        InitializeComponent();
    }

    private DatabaseRuntimePanelViewModel? Panel => DataContext as DatabaseRuntimePanelViewModel;

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

    private void OnTableClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseTableItemViewModel table }
            && Panel is { } panel)
        {
            _ = panel.PreviewTableAsync(table);
        }
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

    private void OnDismissInspectorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.SelectRow(null);
    }

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

    private async Task CopySelectedRowAsync(
        Func<DatabaseRuntimePanelViewModel, DatabaseResultRowViewModel, string> build)
    {
        if (Panel is { SelectedRow: { } row } panel
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(build(panel, row));
        }
    }
}
