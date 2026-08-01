using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Model;
using Dock.Model.Core;
using Dock.Settings;

namespace GhostShell.App.Controls;

/// <summary>
/// Uses a panel's existing title as Dock's drag surface, avoiding a second tab
/// strip above content that already owns complete panel chrome.
/// </summary>
public sealed class PanelDockHandle : ContentControl
{
    public PanelDockHandle()
    {
        // The title glyphs are usually narrower than the header column that owns
        // them. Keep the complete column draggable instead of requiring a grab
        // directly on the text.
        Background = Brushes.Transparent;
        SetValue(DockProperties.IsDragAreaProperty, true);
        SetValue(DockProperties.IsDragEnabledProperty, true);
        AutomationProperties.SetName(this, "Rearrange panel");
        ToolTip.SetTip(this, "Drag to rearrange · double-click to float");
        Loaded += OnLoaded;
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        MarkRenderedSurface();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        // A ContentControl's presenter and content may be attached after its
        // template has been applied (notably while restoring Dock windows).
        // Mark the completed visual tree as well as the template-time tree.
        MarkRenderedSurface();
    }

    private void MarkRenderedSurface()
    {
        // Dock resolves a drag source from the exact visuals returned by its hit
        // test rather than walking to an ancestor. Every rendered layer of the
        // title therefore needs to opt into the same drag surface.
        SetDragArea(this);
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            SetDragArea(control);
        }
    }

    private static void SetDragArea(Control control)
    {
        control.SetValue(DockProperties.IsDragAreaProperty, true);
        control.SetValue(DockProperties.IsDragEnabledProperty, true);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not IDockable
            {
                Owner: IDock { Factory: { } factory },
            } dockable
            || !DockCapabilityResolver.IsEnabled(
                dockable,
                DockCapability.Float,
                DockCapabilityResolver.ResolveOperationDock(dockable)))
        {
            return;
        }

        factory.FloatDockable(dockable);
        factory.ActivateWindow(dockable);
        e.Handled = true;
    }
}
