using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.Components;

/// <summary>
/// Database workspace interaction that belongs to Avalonia controls: responsive
/// panel geometry, dynamic DataGrid columns, active-cell actions, and clipboard.
/// Database operations and edit state remain in the panel view model.
/// </summary>
public sealed partial class DatabaseWorkspaceView : UserControl
{
    private const double ObjectsListMinimumWorkspaceWidth = 520;
    private const double ObjectsListMinimumWidth = 120;
    private const double ResultsMinimumWidth = 240;

    private DatabaseRuntimePanelViewModel? _observedPanel;
    private GridLength _objectsWidth = new(196);
    private bool _objectsFolded;
    private bool _resizingInspector;
    private bool _syncingSelection;
    private double _resizeOriginX;
    private double _resizeOriginWidth;

    public DatabaseWorkspaceView()
    {
        InitializeComponent();
        InitializeDatabaseContextMenu();
        DataContextChanged += (_, _) => ObservePanel();
        // One binding for the expanded editor's lifetime; which cell it edits
        // is its DataContext, set when a prose-sized cell begins editing.
        CellExpandEditor.Bind(
            TextBox.TextProperty,
            new Avalonia.Data.Binding(nameof(DatabaseResultCellViewModel.EditText))
            {
                Mode = Avalonia.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = Avalonia.Data.UpdateSourceTrigger.PropertyChanged,
            });
        ResultDataGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        ObservePanel();
    }

    /// <summary>
    /// A prose-sized cell (text, JSON) grows an expanded editor beside the
    /// cell instead of asking the user to write documents in one grid line.
    /// Both editors stage into the same cell view model, so it does not matter
    /// which one the keystrokes land in.
    /// </summary>
    private void OnPreparingCellForEdit(
        object? sender,
        DataGridPreparingCellForEditEventArgs e)
    {
        _ = sender;
        var ordinal = ResultDataGrid.Columns.IndexOf(e.Column);
        var cell = (e.Row?.DataContext as DatabaseResultRowViewModel)?.Cells
            is { } cells && ordinal >= 0 && ordinal < cells.Count
            ? cells[ordinal]
            : null;
        if (cell?.UsesLargeTextEditor != true)
        {
            CloseCellExpandEditor();
            return;
        }

        CellExpandEditor.DataContext = cell;
        CellExpandPopup.PlacementTarget = e.EditingElement;
        CellExpandPopup.IsOpen = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (CellExpandPopup.IsOpen)
                {
                    CellExpandEditor.Focus();
                    CellExpandEditor.CaretIndex = CellExpandEditor.Text?.Length ?? 0;
                }
            },
            DispatcherPriority.Input);
    }

    private void CloseCellExpandEditor()
    {
        CellExpandPopup.IsOpen = false;
        CellExpandEditor.DataContext = null;
    }

    private void OnCellExpandEditorKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        // Enter closes (Shift+Enter keeps writing lines); Escape closes too —
        // the staged text stays either way, and Revert remains the undo.
        if ((e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            || e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseCellExpandEditor();
            ResultDataGrid.Focus();
        }
    }

    private DatabaseRuntimePanelViewModel? Panel =>
        DataContext as DatabaseRuntimePanelViewModel;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetObjectsFolded(e.NewSize.Width < ObjectsListMinimumWorkspaceWidth);
    }

    private void ObservePanel()
    {
        CloseCellExpandEditor();
        InvalidateDatabaseContextMenu(closePopup: true);
        if (_observedPanel is not null)
        {
            _observedPanel.PropertyChanged -= OnPanelPropertyChanged;
        }

        _observedPanel = Panel;
        if (_observedPanel is not null)
        {
            _observedPanel.PropertyChanged += OnPanelPropertyChanged;
        }

        RebuildResultColumns();
        SyncSelectedRow();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is null
            or nameof(DatabaseRuntimePanelViewModel.ResultRows)
            or nameof(DatabaseRuntimePanelViewModel.ResultColumns)
            or nameof(DatabaseRuntimePanelViewModel.SelectedObject))
        {
            InvalidateDatabaseContextMenu(closePopup: true);
            CloseCellExpandEditor();
        }

        if (e.PropertyName is null or nameof(DatabaseRuntimePanelViewModel.ResultColumns))
        {
            RebuildResultColumns();
        }

        if (e.PropertyName is null or nameof(DatabaseRuntimePanelViewModel.CanSortTable))
        {
            SyncResultColumnSorting();
        }

        if (e.PropertyName is null or nameof(DatabaseRuntimePanelViewModel.SelectedRow))
        {
            SyncSelectedRow();
        }
    }

    private void RebuildResultColumns()
    {
        if (_observedPanel is null)
        {
            ResultDataGrid.Columns.Clear();
            return;
        }

        var replacements = _observedPanel.ResultColumns;
        if (ResultDataGrid.Columns.Count == replacements.Count
            && replacements
                .Select((column, ordinal) => DatabaseDataGridColumnFactory.CanReuse(
                    ResultDataGrid.Columns[ordinal],
                    column))
                .All(canReuse => canReuse))
        {
            for (var ordinal = 0; ordinal < replacements.Count; ordinal++)
            {
                DatabaseDataGridColumnFactory.Refresh(
                    ResultDataGrid.Columns[ordinal],
                    replacements[ordinal],
                    _observedPanel.CanSortTable);
            }

            return;
        }

        ResultDataGrid.Columns.Clear();

        foreach (var column in DatabaseDataGridColumnFactory.Create(
                     replacements,
                     _observedPanel.CanSortTable))
        {
            ResultDataGrid.Columns.Add(column);
        }
    }

    private void SyncResultColumnSorting()
    {
        var canSort = _observedPanel?.CanSortTable == true;
        foreach (var column in ResultDataGrid.Columns)
        {
            column.CanUserSort = canSort;
        }
    }

    private void SyncSelectedRow()
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            ResultDataGrid.SelectedItem = _observedPanel?.SelectedRow;
            if (_observedPanel is { SelectedRow: { } row } panel)
            {
                // ResultRows and SelectedRow are published back-to-back when a
                // row is staged. Let ItemsSource process the new collection
                // before asking the virtualized grid to realize its last row.
                _ = DispatcherTimer.RunOnce(
                    () =>
                    {
                        if (ReferenceEquals(_observedPanel, panel)
                            && ReferenceEquals(panel.SelectedRow, row))
                        {
                            ResultDataGrid.ScrollIntoView(row, null);
                        }
                    },
                    TimeSpan.FromMilliseconds(1),
                    DispatcherPriority.Background);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
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
            // A zero width alone remains clamped by MinWidth, so all three
            // constraints are released while the objects list is folded.
            columns[0].MinWidth = 0;
            columns[0].Width = new GridLength(0);
            columns[1].Width = new GridLength(0);
            columns[2].MinWidth = 0;
        }
        else
        {
            columns[0].MinWidth = ObjectsListMinimumWidth;
            columns[0].Width = _objectsWidth;
            columns[1].Width = new GridLength(5);
            columns[2].MinWidth = ResultsMinimumWidth;
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

    private void OnTableClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseTableItemViewModel table }
            && Panel is { } panel)
        {
            _ = panel.PreviewTableAsync(table);
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

    private async void OnFilterValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Panel is { } panel)
        {
            e.Handled = true;
            await panel.ApplyFilterAsync();
        }
    }

    private async void OnApplyFilterClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            await panel.ApplyFilterAsync();
        }
    }

    private async void OnClearFilterClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            await panel.ClearFilterAsync();
        }
    }

    private async void OnPreviousPageClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            await panel.PreviousPageAsync();
        }
    }

    private async void OnNextPageClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            await panel.NextPageAsync();
        }
    }

    private async void OnPageLimitKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter || Panel is not { } panel)
        {
            return;
        }

        e.Handled = true;
        await panel.ApplyPageLimitAsync();
    }

    private void OnResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        // Moving to another row moves on from the expanded editor too.
        CloseCellExpandEditor();
        if (_syncingSelection || Panel is not { } panel)
        {
            return;
        }

        var selected = ResultDataGrid.SelectedItem as DatabaseResultRowViewModel;
        if (!ReferenceEquals(panel.SelectedRow, selected))
        {
            panel.SelectRow(selected);
        }
    }

    private void OnResultSorting(object? sender, DataGridColumnEventArgs e)
    {
        _ = sender;
        // Avalonia otherwise sorts the current ItemsSource locally after the
        // event returns. The database owns ordering for the complete result set.
        e.Handled = true;
        if (Panel is not { CanSortTable: true } panel
            || e.Column.Tag is not DatabaseResultColumnViewModel column)
        {
            return;
        }

        // File-backed providers can complete the database read synchronously.
        // Replacing DataGrid columns while its header MouseUp route is still
        // unwinding causes re-entrant layout (and an allocation loop in
        // Avalonia Headless). Always let the input route finish first.
        _ = DispatcherTimer.RunOnce(
            () =>
            {
                if (ReferenceEquals(Panel, panel))
                {
                    _ = panel.ToggleTableSortAsync(column.Name);
                }
            },
            TimeSpan.FromMilliseconds(1),
            DispatcherPriority.Background);
    }

    private void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.AddRow();
    }

    private void OnDeleteRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ResultDataGrid.CancelEdit();
        Panel?.DeleteSelectedRow();
    }


    private void OnSetDefaultClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var ordinal = ResultDataGrid.CurrentColumn?.DisplayIndex;
        CommitGridEdit();
        if (ordinal is { } value)
        {
            Panel?.SetSelectedCellDefault(value);
        }
    }

    private async void OnRevertChangesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ResultDataGrid.CancelEdit();
        if (Panel is { } panel)
        {
            await panel.RevertChangesAsync();
        }
    }

    private async void OnSaveChangesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CommitGridEdit();
        if (Panel is { } panel)
        {
            await panel.SaveChangesAsync();
        }
    }

    private void CommitGridEdit()
    {
        ResultDataGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        ResultDataGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
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
        _ = sender;
        _resizingInspector = false;
        e.Pointer.Capture(null);
    }

    private async void OnCopyRowJsonClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await CopySelectedRowAsync(
            static (panel, row) => panel.BuildRowJson(row),
            sender as Button);
    }

    private async void OnCopyRowCsvClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await CopySelectedRowAsync(
            static (panel, row) => panel.BuildRowCsv(row),
            sender as Button);
    }

    private async void OnCopyRowInsertClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        await CopySelectedRowAsync(
            static (panel, row) => panel.BuildRowSqlInsert(row),
            sender as Button);
    }

    private async Task CopySelectedRowAsync(
        Func<DatabaseRuntimePanelViewModel, DatabaseResultRowViewModel, string> build,
        Button? source = null)
    {
        var panel = Panel;
        var row = panel?.SelectedRow;
        CommitGridEdit();
        if (panel is not null && row is not null && IsRowCurrent(panel, row))
        {
            try
            {
                var text = build(panel, row);
                if (await CopyTextAsync(text, panel))
                {
                    ShowCopyFeedback(source);
                }
            }
            catch (OperationCanceledException)
            {
                // Clipboard ownership can disappear during shutdown.
            }
            catch (Exception exception)
            {
                panel.ReportInteractionError(
                    $"Could not format the selected database row: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// The copy answered: the button's label yields to a success check that
    /// fades back once the moment has passed. The styles animate; this only
    /// flips the class.
    /// </summary>
    private static void ShowCopyFeedback(Button? source)
    {
        if (source is null || source.Classes.Contains("copied"))
        {
            return;
        }

        source.Classes.Add("copied");
        _ = DispatcherTimer.RunOnce(
            () => source.Classes.Remove("copied"),
            TimeSpan.FromSeconds(1.2));
    }

    private void OnFieldEditClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if ((sender as Control)?.DataContext is DatabaseRowFieldViewModel field)
        {
            field.BeginEdit();
        }
    }

    private void OnFieldApplyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if ((sender as Control)?.DataContext is DatabaseRowFieldViewModel field)
        {
            field.ApplyEdit();
        }
    }

    private void OnFieldCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if ((sender as Control)?.DataContext is DatabaseRowFieldViewModel field)
        {
            field.CancelEdit();
        }
    }

    private void OnFieldDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DatabaseRowFieldViewModel field)
        {
            return;
        }

        // Enter inserts lines in a multi-line draft; Cmd+Enter applies,
        // Escape reverts — the same grammar as the SQL editor.
        if (e.Key == Key.Enter
            && (e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            e.Handled = true;
            field.ApplyEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            field.CancelEdit();
        }
    }

    private void OnDatabaseHeaderClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CommitGridEdit();
        Panel?.ShowDatabaseOverview();
    }

    private void OnAddFilterRowClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        Panel?.AddFilterRow(
            (sender as Control)?.DataContext as DatabaseFilterRowViewModel);
    }

    private void OnRemoveFilterRowClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Panel is { } panel
            && (sender as Control)?.DataContext is DatabaseFilterRowViewModel row)
        {
            panel.RemoveFilterRow(row);
        }
    }
}
