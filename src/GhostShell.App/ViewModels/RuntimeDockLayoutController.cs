using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc;
using Dock.Model.Inpc.Controls;
using Dock.Serializer.SystemTextJson;
using GhostShell.App.Controls;
using GhostShell.Core;
using GhostShell.Docking;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns the Dock layout for one GhostShell tab.
///
/// Dock owns geometry, drag/drop, proportions, and floating windows. GhostShell
/// owns panel lifetimes. The only durable join between those two graphs is the
/// document id: while a saved layout is still empty it is a layout-slot id; once
/// a live panel fills the leaf it becomes that panel's instance id.
/// </summary>
internal sealed class RuntimeDockLayoutController
{
    private const string RootId = "ghostshell-root";
    private readonly DockSerializer _serializer = DockLayoutSerializer.Create();
    private readonly DockWorkspaceManager _workspaceManager;
    private readonly Dictionary<string, RuntimePanelViewModel> _contexts =
        new(StringComparer.Ordinal);
    private int _revision;

    public RuntimeDockLayoutController(LayoutDefinition? definition)
    {
        Factory = new RuntimeDockFactory(ResolveContext);
        Layout = Restore(definition?.DockLayoutJson)
            ?? CreateLayoutFromSlots(definition)
            ?? CreateEmptyLayout();
        // The factory has to be able to say where "back" is. A floated panel's own
        // root knows only that it is in a window; the window it came from is this
        // one, and nothing in the Dock graph reliably points at it from there.
        Factory.HomeLayout = Layout;

        // Establish model ownership now, but do not allocate native hosts yet.
        // Recovery builds this graph while the launcher and its modal recovery
        // dialog are still transitioning. Native windows belong to the mounted
        // workspace, not to construction of its view model.
        Factory.InitModel(Layout);
        _workspaceManager = new DockWorkspaceManager(_serializer);
        _workspaceManager.WorkspaceDirtyChanged += OnWorkspaceDirtyChanged;
        _workspaceManager.TrackLayout(Layout);
        Factory.LayoutMutated += OnFactoryLayoutMutated;
    }

    public event EventHandler? LayoutChanged;

    public static string SerializeDefinition(LayoutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new RuntimeDockLayoutController(definition).Serialize();
    }

    public RuntimeDockFactory Factory { get; }

    public IRootDock Layout { get; }

    public int Revision => _revision;

    public string Serialize() => DockLayoutPayloadCodec.Encode(
        _serializer.Serialize<IRootDock>(Layout));

    public void Attach(
        RuntimePanelViewModel panel,
        string? savedDockableId = null,
        PanelSplitOrientation? split = null,
        PanelInstanceId? targetPanelId = null)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var document = savedDockableId is { Length: > 0 }
            ? FindDocument(savedDockableId)
            : null;
        if (document is not null)
        {
            Bind(document, panel, savedDockableId!);
            Changed();
            return;
        }

        var leaf = CreateLeaf(panel, panel.Id.Value);
        var target = targetPanelId is { } explicitTarget
            ? FindLeaf(explicitTarget.Value)
            : FindLeaf(panel.Id.Value);
        target ??= FindLeafForContext(
            _contexts.Values.LastOrDefault(item => item.Id != panel.Id));
        if (target is null)
        {
            Layout.VisibleDockables ??= Factory.CreateList<IDockable>();
            Factory.AddDockable(Layout, leaf);
            Layout.ActiveDockable = leaf;
        }
        else
        {
            Factory.SplitToDock(
                target,
                leaf,
                split == PanelSplitOrientation.TopBottom
                    ? DockOperation.Bottom
                    : DockOperation.Right);
        }

        Activate(panel.Id);
        Changed();
    }

    public void AttachToEdge(RuntimePanelViewModel panel, PanelSide side)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var leaf = CreateLeaf(panel, panel.Id.Value);
        var body = Layout.VisibleDockables?
            .FirstOrDefault(dockable => dockable is not ProportionalDockSplitter) as IDock;
        if (body is null)
        {
            Layout.VisibleDockables ??= Factory.CreateList<IDockable>();
            Factory.AddDockable(Layout, leaf);
            Layout.ActiveDockable = leaf;
        }
        else
        {
            Factory.SplitToDock(body, leaf, side switch
            {
                PanelSide.Left => DockOperation.Left,
                PanelSide.Right => DockOperation.Right,
                PanelSide.Top => DockOperation.Top,
                PanelSide.Bottom => DockOperation.Bottom,
                _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
            });
        }

        Activate(panel.Id);
        Changed();
    }

    public void ReplacePlaceholder(
        RuntimePanelViewModel placeholder,
        RuntimePanelViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        ArgumentNullException.ThrowIfNull(replacement);
        var document = FindDocument(placeholder.Id.Value);
        if (document is null)
        {
            Attach(replacement);
            return;
        }

        _contexts.Remove(placeholder.Id.Value);
        Bind(document, replacement, placeholder.Id.Value);
        Changed();
    }

    public void Rebind(RuntimePanelViewModel current, RuntimePanelViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        var document = FindDocument(current.Id.Value);
        if (document is null)
        {
            Attach(replacement);
            return;
        }

        _contexts.Remove(current.Id.Value);
        Bind(document, replacement, current.Id.Value);
        Changed();
    }

    public void Activate(PanelInstanceId panelId)
    {
        var document = FindDocument(panelId.Value);
        if (document?.Owner is not IDock owner)
        {
            return;
        }

        owner.ActiveDockable = document;
        Layout.ActiveDockable = FindTopLevel(owner) ?? Layout.ActiveDockable;
    }

    public PanelInstanceId? FindPanel(
        PanelInstanceId activePanelId,
        PanelFocusDirection direction)
    {
        var regions = BuildPanelRegions();
        if (!regions.TryGetValue(activePanelId, out var active)
            || regions.Count < 2)
        {
            return null;
        }

        var ordered = regions.Values.OrderBy(region => region.Order).ToArray();
        if (direction == PanelFocusDirection.Next)
        {
            var index = Array.FindIndex(
                ordered,
                region => region.PanelId == activePanelId);
            return ordered[(index + 1) % ordered.Length].PanelId;
        }

        return ordered
            .Where(candidate => candidate.PanelId != activePanelId)
            .Where(candidate => IsInDirection(active.Bounds, candidate.Bounds, direction))
            .OrderBy(candidate => CrossAxisOverlaps(active.Bounds, candidate.Bounds, direction) ? 0 : 1)
            .ThenBy(candidate => PrimaryDistance(active.Bounds, candidate.Bounds, direction))
            .ThenBy(candidate => CrossAxisDistance(active.Bounds, candidate.Bounds, direction))
            .ThenBy(candidate => candidate.Order)
            .Select(candidate => (PanelInstanceId?)candidate.PanelId)
            .FirstOrDefault();
    }

    public void Remove(PanelInstanceId panelId)
    {
        var document = FindDocument(panelId.Value);
        if (document is null)
        {
            return;
        }

        _contexts.Remove(panelId.Value);
        // Documents deliberately advertise CanClose=false so Dock chrome cannot
        // bypass GhostShell's busy-session confirmation. Once that confirmation
        // has completed, the controller owns the authoritative removal and must
        // not route through Dock's guarded CloseDockable path.
        var leaf = document.Owner as IDock;
        if (leaf is not null
            && leaf.VisibleDockables?.Count == 1
            && leaf.Owner is IDock)
        {
            Factory.RemoveDockable(leaf, collapse: true);
        }
        else
        {
            Factory.RemoveDockable(document, collapse: true);
        }
        Changed();
    }

    private object? ResolveContext(string id) =>
        _contexts.TryGetValue(id, out var panel) ? panel : null;

    private void Bind(IDocument document, RuntimePanelViewModel panel, string oldId)
    {
        _contexts.Remove(oldId);
        document.Id = panel.Id.Value;
        document.Title = panel.Title;
        document.Context = panel;
        document.CanClose = false;
        document.CanFloat = true;
        document.CanDrag = true;
        document.CanDrop = true;
        document.MinWidth = Math.Max(1, panel.LayoutMinimumWidth);
        document.MinHeight = Math.Max(1, panel.LayoutMinimumHeight);
        _contexts[panel.Id.Value] = panel;
        Factory.ContextLocator![panel.Id.Value] = () => panel;
        if (document.Owner is IDock owner)
        {
            owner.ActiveDockable = document;
        }
    }

    private DocumentDock CreateLeaf(RuntimePanelViewModel panel, string id)
    {
        var document = new Document();
        Bind(document, panel, id);
        return CreateLeaf(document, $"panel-dock-{panel.Id.Value}");
    }

    private DocumentDock CreateLeaf(IDocument document, string id) =>
        Factory.CreateLeaf(document, id);

    private IRootDock? Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var restored = _serializer.Deserialize<IRootDock>(
                DockLayoutPayloadCodec.Decode(json));
            if (restored is not null)
            {
                NormalizeRestoredLayout(restored);
            }

            return restored;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException
                or InvalidDataException
                or NotSupportedException
                or InvalidOperationException)
        {
            return null;
        }
    }

    internal static void NormalizeRestoredLayout(IRootDock root)
    {
        NormalizeSelections(root);
        foreach (var window in root.Windows ?? [])
        {
            if (window.Layout is not { } windowRoot)
            {
                continue;
            }

            // Dock's generated System.Text.Json converter round-trips the
            // selection properties as equivalent objects rather than as the
            // instances held by VisibleDockables. DockControl renders the
            // selected instance, so leaving that detached copy in place gives
            // a restored native window with an empty body.
            windowRoot.Window = window;
            NormalizeSelections(windowRoot);
        }
    }

    private static void NormalizeSelections(IDock dock)
    {
        var children = dock.VisibleDockables?
            .Where(child => child is not ProportionalDockSplitter)
            .ToArray()
            ?? [];

        foreach (var childDock in children.OfType<IDock>())
        {
            NormalizeSelections(childDock);
        }

        dock.ActiveDockable = ResolveSelection(dock.ActiveDockable, children)
            ?? children.FirstOrDefault();
        dock.DefaultDockable = ResolveSelection(dock.DefaultDockable, children)
            ?? dock.ActiveDockable;

        if (dock is IRootDock root && root.FocusedDockable is { } focused)
        {
            root.FocusedDockable = FindDescendant(root, focused) ?? focused;
        }
    }

    private static IDockable? ResolveSelection(
        IDockable? selection,
        IReadOnlyList<IDockable> children)
    {
        if (selection is null)
        {
            return null;
        }

        var sameInstance = children.FirstOrDefault(child => ReferenceEquals(child, selection));
        if (sameInstance is not null)
        {
            return sameInstance;
        }

        if (!string.IsNullOrWhiteSpace(selection.Id))
        {
            var sameId = children.FirstOrDefault(child =>
                child.GetType() == selection.GetType()
                && string.Equals(child.Id, selection.Id, StringComparison.Ordinal));
            if (sameId is not null)
            {
                return sameId;
            }
        }

        var sameType = children.Where(child => child.GetType() == selection.GetType()).ToArray();
        return sameType.Length == 1 ? sameType[0] : null;
    }

    private static IDockable? FindDescendant(IDock root, IDockable selection)
    {
        var matches = new List<IDockable>();
        var pending = new Stack<IDockable>(root.VisibleDockables ?? []);
        while (pending.Count > 0)
        {
            var candidate = pending.Pop();
            if (candidate.GetType() == selection.GetType()
                && string.Equals(candidate.Id, selection.Id, StringComparison.Ordinal))
            {
                matches.Add(candidate);
            }

            if (candidate is IDock { VisibleDockables: { } children })
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private IRootDock? CreateLayoutFromSlots(LayoutDefinition? definition)
    {
        if (definition?.Slots.Count is not > 0)
        {
            return null;
        }

        var rows = definition.Slots
            .GroupBy(slot => slot.Bounds.Row)
            .OrderBy(group => group.Key)
            .Select(group => CreateSlotRow(group.OrderBy(slot => slot.Bounds.Column)))
            .Cast<IDockable>()
            .ToArray();
        IDockable body;
        if (rows.Length == 1)
        {
            body = rows[0];
        }
        else
        {
            body = CreateProportional(Orientation.Vertical, rows);
        }

        var root = CreateEmptyLayout();
        root.VisibleDockables = Factory.CreateList(body);
        root.ActiveDockable = body;
        return root;
    }

    private IDockable CreateSlotRow(IEnumerable<LayoutSlotDefinition> slots)
    {
        var leaves = slots.Select(slot =>
        {
            var document = new Document
            {
                Id = slot.Id.Value,
                Title = "Empty panel",
                CanClose = false,
                CanFloat = true,
                CanDrag = true,
                CanDrop = true,
                MinWidth = slot.MinimumSize.Width,
                MinHeight = slot.MinimumSize.Height,
            };
            return (IDockable)CreateLeaf(document, $"slot-dock-{slot.Id.Value}");
        }).ToArray();

        return leaves.Length == 1
            ? leaves[0]
            : CreateProportional(Orientation.Horizontal, leaves);
    }

    private ProportionalDock CreateProportional(
        Orientation orientation,
        IReadOnlyList<IDockable> children)
    {
        var visible = new List<IDockable>(children.Count * 2 - 1);
        for (var index = 0; index < children.Count; index++)
        {
            if (index > 0)
            {
                visible.Add(new ProportionalDockSplitter
                {
                    CanResize = true,
                    ResizePreview = false,
                });
            }

            children[index].Proportion = 1d / children.Count;
            visible.Add(children[index]);
        }

        return new ProportionalDock
        {
            Orientation = orientation,
            IsCollapsable = false,
            VisibleDockables = Factory.CreateList(visible.ToArray()),
            ActiveDockable = children[0],
        };
    }

    private IRootDock CreateEmptyLayout()
    {
        var root = (IRootDock)Factory.CreateRootDock();
        root.Id = RootId;
        root.Title = "GhostShell workspace";
        root.IsCollapsable = false;
        root.VisibleDockables = Factory.CreateList<IDockable>();
        root.Windows = Factory.CreateList<IDockWindow>();
        return root;
    }

    private IDocument? FindDocument(string id) =>
        EnumerateDockables(Layout).OfType<IDocument>()
            .FirstOrDefault(document => string.Equals(document.Id, id, StringComparison.Ordinal));

    private IDock? FindLeaf(string panelId) => FindDocument(panelId)?.Owner as IDock;

    private IDock? FindLeafForContext(RuntimePanelViewModel? panel) =>
        panel is null ? null : FindLeaf(panel.Id.Value);

    private IDockable? FindTopLevel(IDockable dockable)
    {
        var current = dockable;
        while (current.Owner is not null && current.Owner != Layout)
        {
            current = current.Owner;
        }

        return current.Owner == Layout ? current : null;
    }

    private Dictionary<PanelInstanceId, PanelRegion> BuildPanelRegions()
    {
        var regions = new Dictionary<PanelInstanceId, PanelRegion>();
        var order = 0;
        foreach (var dockable in Layout.VisibleDockables ?? [])
        {
            AddRegions(dockable, new DockRegion(0, 0, 1, 1), regions, ref order);
        }

        return regions;
    }

    private static void AddRegions(
        IDockable dockable,
        DockRegion bounds,
        IDictionary<PanelInstanceId, PanelRegion> regions,
        ref int order)
    {
        if (dockable is IDocument { Context: RuntimePanelViewModel panel })
        {
            regions[panel.Id] = new PanelRegion(panel.Id, bounds, order++);
            return;
        }

        if (dockable is not IDock { VisibleDockables: { } children })
        {
            return;
        }

        var panelDocks = children
            .Where(child => child is not ProportionalDockSplitter)
            .ToArray();
        if (dockable is not ProportionalDock proportional || panelDocks.Length < 2)
        {
            foreach (var child in panelDocks)
            {
                AddRegions(child, bounds, regions, ref order);
            }

            return;
        }

        var weights = panelDocks
            .Select(panelDock => double.IsFinite(panelDock.Proportion) && panelDock.Proportion > 0
                ? panelDock.Proportion
                : 1d)
            .ToArray();
        var total = weights.Sum();
        var offset = 0d;
        for (var index = 0; index < panelDocks.Length; index++)
        {
            var share = weights[index] / total;
            var childBounds = proportional.Orientation == Orientation.Horizontal
                ? new DockRegion(
                    bounds.X + (bounds.Width * offset),
                    bounds.Y,
                    bounds.Width * share,
                    bounds.Height)
                : new DockRegion(
                    bounds.X,
                    bounds.Y + (bounds.Height * offset),
                    bounds.Width,
                    bounds.Height * share);
            AddRegions(panelDocks[index], childBounds, regions, ref order);
            offset += share;
        }
    }

    private static bool IsInDirection(
        DockRegion origin,
        DockRegion candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left => candidate.CenterX < origin.CenterX,
            PanelFocusDirection.Right => candidate.CenterX > origin.CenterX,
            PanelFocusDirection.Up => candidate.CenterY < origin.CenterY,
            PanelFocusDirection.Down => candidate.CenterY > origin.CenterY,
            _ => false,
        };

    private static bool CrossAxisOverlaps(
        DockRegion origin,
        DockRegion candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right =>
                origin.Y < candidate.Bottom && candidate.Y < origin.Bottom,
            PanelFocusDirection.Up or PanelFocusDirection.Down =>
                origin.X < candidate.Right && candidate.X < origin.Right,
            _ => false,
        };

    private static double PrimaryDistance(
        DockRegion origin,
        DockRegion candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right =>
                Math.Abs(candidate.CenterX - origin.CenterX),
            PanelFocusDirection.Up or PanelFocusDirection.Down =>
                Math.Abs(candidate.CenterY - origin.CenterY),
            _ => 0,
        };

    private static double CrossAxisDistance(
        DockRegion origin,
        DockRegion candidate,
        PanelFocusDirection direction) => direction switch
        {
            PanelFocusDirection.Left or PanelFocusDirection.Right =>
                Math.Abs(candidate.CenterY - origin.CenterY),
            PanelFocusDirection.Up or PanelFocusDirection.Down =>
                Math.Abs(candidate.CenterX - origin.CenterX),
            _ => 0,
        };

    private static IEnumerable<IDockable> EnumerateDockables(IRootDock root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        foreach (var window in root.Windows ?? [])
        {
            if (window.Layout is not null)
            {
                pending.Push(window.Layout);
            }
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is not IDock dock || dock.VisibleDockables is null)
            {
                continue;
            }

            for (var index = dock.VisibleDockables.Count - 1; index >= 0; index--)
            {
                pending.Push(dock.VisibleDockables[index]);
            }
        }
    }

    private void OnWorkspaceDirtyChanged(
        object? sender,
        DockWorkspaceDirtyChangedEventArgs eventArgs)
    {
        if (!eventArgs.IsDirty)
        {
            return;
        }

        Changed();
        _workspaceManager.MarkClean();
    }

    private void OnFactoryLayoutMutated(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Changed();
    }

    private void Changed()
    {
        _revision++;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly record struct PanelRegion(
        PanelInstanceId PanelId,
        DockRegion Bounds,
        int Order);

    private readonly record struct DockRegion(
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;

        public double CenterX => X + (Width / 2d);

        public double CenterY => Y + (Height / 2d);
    }
}

internal sealed class RuntimeDockFactory : Factory
{
    private readonly Func<string, object?> _contextResolver;
    private bool _initializingModel;

    public RuntimeDockFactory(Func<string, object?> contextResolver)
    {
        _contextResolver = contextResolver;
        ContextLocator = new Dictionary<string, Func<object?>>();
        Func<IHostWindow?> hostFactory = static () => new RuntimePanelHostWindow();
        DefaultHostWindowLocator = hostFactory;
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = hostFactory,
        };
    }

    public event EventHandler? LayoutMutated;

    /// <summary>
    /// The layout a floated panel belongs to.
    ///
    /// A panel in a window of its own is the root of its own tree, and the tree it
    /// left is not reachable from it: the Dock graph points from the workspace to
    /// its windows and never back. So the factory is told, once, which layout it
    /// serves — the tab that built it.
    /// </summary>
    internal IRootDock? HomeLayout { get; set; }

    /// <summary>
    /// The geometry that holds one panel.
    ///
    /// A leaf is only where a panel currently sits, never part of its identity:
    /// floating a panel and putting it back both build a new one around the same
    /// document. When the panel leaves, the leaf collapses rather than staying
    /// behind as a permanent "No documents open" hole in the workspace.
    /// </summary>
    internal DocumentDock CreateLeaf(IDocument document, string id) => new()
    {
        Id = id,
        Title = document.Title,
        IsCollapsable = true,
        CanCloseLastDockable = true,
        CanCreateDocument = false,
        EnableWindowDrag = true,
        ActiveDockable = document,
        VisibleDockables = CreateList<IDockable>(document),
    };

    /// <summary>
    /// Whether this panel is currently in a window of its own.
    /// </summary>
    public bool IsFloating(IDockable dockable)
    {
        ArgumentNullException.ThrowIfNull(dockable);
        return FindRoot(dockable, _ => true) is { Window: not null } root
            && !ReferenceEquals(root, HomeLayout);
    }

    /// <summary>
    /// Puts a floated panel back into the workspace it came from.
    ///
    /// Floating had no way back. A panel left the workspace on a double-click
    /// nobody was told about and could only return by being dragged onto a
    /// placement target — which, over a browser, was not drawn at all. Both
    /// directions are one button in the panel's header now, and this is the half
    /// Dock does not offer: its own vocabulary can float a dockable but has no
    /// word for the workspace a floating window belongs to.
    ///
    /// The panel keeps its document — its identity, its context, and so its
    /// session. Only the geometry around it is rebuilt.
    /// </summary>
    public bool DockBack(IDockable dockable)
    {
        ArgumentNullException.ThrowIfNull(dockable);
        if (dockable is not IDocument document
            || HomeLayout is not { } home
            || FindRoot(dockable, _ => true) is not { Window: { } window } floating
            || ReferenceEquals(floating, home))
        {
            return false;
        }

        // Resolved before the removal, so the search never meets the leaf that is
        // in the middle of collapsing.
        var target = FindDocumentLeaf(home, document);
        RemoveDockable(document, collapse: true);

        var leaf = CreateLeaf(document, $"panel-dock-{document.Id}");
        if (target is null)
        {
            home.VisibleDockables ??= CreateList<IDockable>();
            AddDockable(home, leaf);
            home.ActiveDockable = leaf;
        }
        else
        {
            SplitToDock(target, leaf, DockOperation.Right);
        }

        // The document arrives owned by the leaf it has just left. Nothing else
        // re-points it: a leaf built around a document states where the document
        // is, and the document has to agree, or activation and removal both go
        // looking for it in the window that is about to close.
        InitDockable(document, leaf);
        leaf.ActiveDockable = document;

        if (home.Windows?.Contains(window) == true
            && floating.VisibleDockables is null or { Count: 0 })
        {
            RemoveWindow(window);
        }

        SetActiveDockable(document);
        NotifyLayoutMutated();
        return true;
    }

    /// <summary>
    /// The leaf holding some other panel, to place a returning one beside.
    /// </summary>
    private static IDock? FindDocumentLeaf(IDock dock, IDockable exclude)
    {
        foreach (var child in dock.VisibleDockables ?? [])
        {
            if (child is IDocument && !ReferenceEquals(child, exclude))
            {
                return dock;
            }

            if (child is IDock nested && FindDocumentLeaf(nested, exclude) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// GhostShell deliberately keeps one runtime panel in each document leaf.
    /// Dock's stock center-drop operation moves a document into the target leaf,
    /// which creates a hidden tab group and makes the dragged panel appear to
    /// cover the panel that was already there. For two occupied leaves, center
    /// drop means exchanging their positions instead.
    /// </summary>
    public override void MoveDockable(
        IDock sourceDock,
        IDock targetDock,
        IDockable sourceDockable,
        IDockable? targetDockable)
    {
        if (!ReferenceEquals(sourceDock, targetDock)
            && sourceDockable is IDocument
            && targetDockable is IDocument targetDocument
            && HasSingleDocument(sourceDock, sourceDockable)
            && HasSingleDocument(targetDock, targetDocument))
        {
            SwapDockable(sourceDock, targetDock, sourceDockable, targetDocument);
            return;
        }

        base.MoveDockable(sourceDock, targetDock, sourceDockable, targetDockable);
    }

    internal void InitModel(IDockable layout)
    {
        _initializingModel = true;
        try
        {
            InitDockable(layout, owner: null);
        }
        finally
        {
            _initializingModel = false;
        }
    }

    public override void OnDockableMoved(IDockable? dockable)
    {
        base.OnDockableMoved(dockable);
        NotifyLayoutMutated();
    }

    public override void OnDockableDocked(
        IDockable? dockable,
        DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
        NotifyLayoutMutated();
    }

    public override void OnDockableUndocked(
        IDockable? dockable,
        DockOperation operation)
    {
        base.OnDockableUndocked(dockable, operation);
        NotifyLayoutMutated();
    }

    public override void OnDockableSwapped(IDockable? dockable)
    {
        base.OnDockableSwapped(dockable);
        NotifyLayoutMutated();
    }

    public override void OnWindowAdded(IDockWindow? window)
    {
        base.OnWindowAdded(window);
        NotifyLayoutMutated();
    }

    public override void OnWindowRemoved(IDockWindow? window)
    {
        base.OnWindowRemoved(window);
        NotifyLayoutMutated();
    }

    public override void OnWindowMoveDragEnd(IDockWindow? window)
    {
        base.OnWindowMoveDragEnd(window);
        NotifyLayoutMutated();
    }

    public override void InitLayout(IDockable layout)
    {
        foreach (var dockable in Find(_ => true))
        {
            if (!string.IsNullOrWhiteSpace(dockable.Id))
            {
                var id = dockable.Id;
                ContextLocator![id] = () => _contextResolver(id);
            }
        }

        base.InitLayout(layout);
    }

    public override void InitDockWindow(IDockWindow window, IDockable? owner)
    {
        if (_initializingModel)
        {
            base.InitDockWindow(window, owner, hostWindow: null);
            return;
        }

        base.InitDockWindow(window, owner);
    }

    private void NotifyLayoutMutated() =>
        LayoutMutated?.Invoke(this, EventArgs.Empty);

    private static bool HasSingleDocument(IDock dock, IDockable document) =>
        dock.VisibleDockables is { Count: 1 } visible
        && ReferenceEquals(visible[0], document);
}
