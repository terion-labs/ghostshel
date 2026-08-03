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

    /// <summary>
    /// Below this width the objects list and the result grid cannot both hold a
    /// usable width, so the list folds into a picker beside the statement
    /// editor and gives its space to the results.
    /// </summary>
    private const double ObjectsListMinimumWorkspaceWidth = 520;

    /// <summary>
    /// The width the objects list had while it was shown, so folding and
    /// unfolding does not discard a width the user chose with the splitter.
    /// </summary>
    private GridLength _objectsWidth = new(196);
    private bool _objectsFolded;

    public DatabaseWorkspaceView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetObjectsFolded(e.NewSize.Width < ObjectsListMinimumWorkspaceWidth);
    }

    private void SetObjectsFolded(bool folded)
    {
        if (folded == _objectsFolded)
        {
            return;
        }

        var columns = WorkspaceGrid.ColumnDefinitions;
        if (folded)
        {
            _objectsWidth = columns[0].Width;
            columns[0].Width = new GridLength(0);
            columns[1].Width = new GridLength(0);
        }
        else
        {
            columns[0].Width = _objectsWidth;
            columns[1].Width = new GridLength(5);
        }

        _objectsFolded = folded;
        ObjectsSidebar.IsVisible = !folded;
        ObjectsSplitter.IsVisible = !folded;
        ObjectsPicker.IsVisible = folded;
    }

    private void OnPickedTableClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseTableItemViewModel table }
            && Panel is { } panel)
        {
            ObjectsPicker.Flyout?.Hide();
            _ = panel.PreviewTableAsync(table);
        }
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
