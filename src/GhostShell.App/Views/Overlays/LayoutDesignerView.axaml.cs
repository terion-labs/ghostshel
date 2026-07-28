using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.Overlays;

internal readonly record struct LayoutDesignerGridSize(
    decimal? Rows,
    decimal? Columns);

public sealed partial class LayoutDesignerView : UserControl
{
    public LayoutDesignerView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddSlotRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<KeyEventArgs>? DesignerKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? EditLayoutRequested;

    public event EventHandler<NumericUpDownValueChangedEventArgs>?
        GridSizeChangedRequested;

    public event EventHandler<RoutedEventArgs>? GrowBottomRequested;

    public event EventHandler<RoutedEventArgs>? GrowLeftRequested;

    public event EventHandler<RoutedEventArgs>? GrowRightRequested;

    public event EventHandler<RoutedEventArgs>? GrowTopRequested;

    public event EventHandler<RoutedEventArgs>? MoveDownRequested;

    public event EventHandler<RoutedEventArgs>? MoveEarlierRequested;

    public event EventHandler<RoutedEventArgs>? MoveLaterRequested;

    public event EventHandler<RoutedEventArgs>? MoveLeftRequested;

    public event EventHandler<RoutedEventArgs>? MoveRightRequested;

    public event EventHandler<RoutedEventArgs>? MoveUpRequested;

    public event EventHandler<RoutedEventArgs>? RemoveSlotRequested;

    public event EventHandler<RoutedEventArgs>? ResetRequested;

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    public event EventHandler<RoutedEventArgs>? ShrinkBottomRequested;

    public event EventHandler<RoutedEventArgs>? ShrinkLeftRequested;

    public event EventHandler<RoutedEventArgs>? ShrinkRightRequested;

    public event EventHandler<RoutedEventArgs>? ShrinkTopRequested;

    public event EventHandler<RoutedEventArgs>? SlotSelectedRequested;

    public event EventHandler<RoutedEventArgs>? PaintPanelRequested;

    internal void FocusNameEditor() =>
        NewLayoutName.Focus(NavigationMethod.Tab);

    internal LayoutDesignerGridSize CaptureGridSize() =>
        new(LayoutRowsPicker.Value, LayoutColumnsPicker.Value);

    internal void FocusGrid() =>
        LayoutDesignerGrid.Focus();

    internal bool CancelPointerGesture() =>
        LayoutDesignerGrid
            .GetVisualDescendants()
            .OfType<LayoutDesignerPreviewPanel>()
            .FirstOrDefault()
            ?.CancelPointerGesture() == true;

    internal void FocusSlot(LayoutDesignerSlotViewModel slot) =>
        this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.DataContext, slot))
            ?.Focus(NavigationMethod.Directional);

    private void OnLayoutAddSlotClick(object? sender, RoutedEventArgs e) =>
        AddSlotRequested?.Invoke(sender, e);

    private void OnCloseOverlayClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnLayoutDesignerKeyDown(object? sender, KeyEventArgs e) =>
        DesignerKeyDownRequested?.Invoke(sender, e);

    private void OnEditLayoutClick(object? sender, RoutedEventArgs e) =>
        EditLayoutRequested?.Invoke(sender, e);

    private void OnLayoutGridSizeChanged(
        object? sender,
        NumericUpDownValueChangedEventArgs e) =>
        GridSizeChangedRequested?.Invoke(sender, e);

    private void OnLayoutGrowBottomClick(object? sender, RoutedEventArgs e) =>
        GrowBottomRequested?.Invoke(sender, e);

    private void OnLayoutGrowLeftClick(object? sender, RoutedEventArgs e) =>
        GrowLeftRequested?.Invoke(sender, e);

    private void OnLayoutGrowRightClick(object? sender, RoutedEventArgs e) =>
        GrowRightRequested?.Invoke(sender, e);

    private void OnLayoutGrowTopClick(object? sender, RoutedEventArgs e) =>
        GrowTopRequested?.Invoke(sender, e);

    private void OnLayoutMoveDownClick(object? sender, RoutedEventArgs e) =>
        MoveDownRequested?.Invoke(sender, e);

    private void OnLayoutMoveEarlierClick(object? sender, RoutedEventArgs e) =>
        MoveEarlierRequested?.Invoke(sender, e);

    private void OnLayoutMoveLaterClick(object? sender, RoutedEventArgs e) =>
        MoveLaterRequested?.Invoke(sender, e);

    private void OnLayoutMoveLeftClick(object? sender, RoutedEventArgs e) =>
        MoveLeftRequested?.Invoke(sender, e);

    private void OnLayoutMoveRightClick(object? sender, RoutedEventArgs e) =>
        MoveRightRequested?.Invoke(sender, e);

    private void OnLayoutMoveUpClick(object? sender, RoutedEventArgs e) =>
        MoveUpRequested?.Invoke(sender, e);

    private void OnLayoutRemoveSlotClick(object? sender, RoutedEventArgs e) =>
        RemoveSlotRequested?.Invoke(sender, e);

    private void OnResetLayoutClick(object? sender, RoutedEventArgs e) =>
        ResetRequested?.Invoke(sender, e);

    private void OnSaveLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(sender, e);

    private void OnLayoutShrinkBottomClick(object? sender, RoutedEventArgs e) =>
        ShrinkBottomRequested?.Invoke(sender, e);

    private void OnLayoutShrinkLeftClick(object? sender, RoutedEventArgs e) =>
        ShrinkLeftRequested?.Invoke(sender, e);

    private void OnLayoutShrinkRightClick(object? sender, RoutedEventArgs e) =>
        ShrinkRightRequested?.Invoke(sender, e);

    private void OnLayoutShrinkTopClick(object? sender, RoutedEventArgs e) =>
        ShrinkTopRequested?.Invoke(sender, e);

    private void OnLayoutSlotClick(object? sender, RoutedEventArgs e) =>
        SlotSelectedRequested?.Invoke(sender, e);

    private void OnLayoutPaintPanelClick(object? sender, RoutedEventArgs e) =>
        PaintPanelRequested?.Invoke(sender, e);
}
