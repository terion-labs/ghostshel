using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
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

    private IDisposable? _dockableBinding;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        MarkRenderedSurface();
    }

    /// <summary>
    /// Points this handle at the dockable it belongs to.
    ///
    /// Dock reads the drag source's own data context to decide what is being
    /// dragged, so a drag surface whose context is something else is not a drag
    /// surface at all — the pointer goes down on the title and nothing happens.
    /// Each panel view used to set this by hand. It cannot: the handle is
    /// declared inside a control template now, and a template does not know where
    /// it will be used. So the handle finds the dockable and follows it.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _dockableBinding?.Dispose();
        _dockableBinding = this.FindAncestorOfType<DockableControl>() is { } host
            ? Bind(DataContextProperty, host.GetObservable(DataContextProperty))
            : null;
        MarkRenderedSurface();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dockableBinding?.Dispose();
        _dockableBinding = null;
        base.OnDetachedFromVisualTree(e);
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

    /// <summary>
    /// Finds the dockable this handle belongs to.
    ///
    /// It used to be the handle's data context, which every panel view had to
    /// remember to re-point at the dockable — and which a handle declared inside a
    /// control template cannot set at all, because the template does not know
    /// where it will be used. The dockable is an ancestor either way, so the
    /// handle asks for it rather than being told.
    /// </summary>
    private IDockable? ResolveDockable() =>
        DataContext as IDockable
        ?? this.FindAncestorOfType<DockableControl>()?.DataContext as IDockable;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResolveDockable() is not
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
