using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

internal sealed class WorkspaceGraphRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<WindowInstanceId, ClientId> _clientByWindow = [];
    private readonly Dictionary<WindowInstanceId, WorkspaceInstanceId> _workspaceByWindow = [];
    private readonly Dictionary<WorkspaceInstanceId, HostedWorkspaceGraph> _workspaces = [];
    private readonly int _eventRetention;
    private readonly TimeProvider _timeProvider;

    public WorkspaceGraphRegistry(
        int eventRetention,
        TimeProvider timeProvider)
    {
        if (eventRetention < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRetention));
        }

        ArgumentNullException.ThrowIfNull(timeProvider);
        _eventRetention = eventRetention;
        _timeProvider = timeProvider;
    }

    public long CurrentRevision(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _workspaces.TryGetValue(workspaceId, out var graph)
                ? graph.Revision
                : 0;
        }
    }

    public long CurrentRegistrationRevision(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            if (_workspaces.TryGetValue(workspaceId, out var requested))
            {
                return requested.Revision;
            }

            return TryGetByWindowUnsafe(windowId, out var owned)
                ? owned.Revision
                : 0;
        }
    }

    public HostResult<WorkspaceGraphSnapshot> RegisterOrReplace(
        RegisterWorkspaceGraphRequest request,
        ClientId? ownerClientId,
        long? expectedRevision,
        IReadOnlyList<SessionDescriptor> liveSessions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(liveSessions);
        lock (_gate)
        {
            _workspaces.TryGetValue(request.Workspace.Id, out var requested);
            if (requested is not null && requested.WindowId != request.WindowId)
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The workspace instance ID is already owned by another window."),
                    requested.Revision);
            }

            _ = TryGetByWindowUnsafe(request.WindowId, out var owned);
            var currentRevision = requested?.Revision ?? owned?.Revision ?? 0;

            if (ownerClientId is { } clientId
                && _clientByWindow.TryGetValue(request.WindowId, out var currentClientId)
                && currentClientId != clientId)
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The window runtime graph is already owned by another client."),
                    currentRevision);
            }

            if (expectedRevision is { } expected && expected != currentRevision)
            {
                return RevisionConflict<WorkspaceGraphSnapshot>(currentRevision, expected);
            }

            if (!TryReconcileSessionLinks(
                    request.WindowId,
                    request.Workspace,
                    liveSessions,
                    requested?.Snapshot().Workspace,
                    out var reconciledWorkspace,
                    out var reconciliationError))
            {
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    reconciliationError!,
                    currentRevision);
            }

            if (requested is not null)
            {
                if (ownerClientId is { } replacementClientId)
                {
                    _clientByWindow[request.WindowId] = replacementClientId;
                }

                return requested.Replace(reconciledWorkspace, expectedRevision);
            }

            var replacement = new HostedWorkspaceGraph(
                request.WindowId,
                reconciledWorkspace,
                _eventRetention,
                _timeProvider);
            if (owned is not null)
            {
                _workspaces.Remove(owned.WorkspaceId);
                owned.Remove();
            }

            _workspaces.Add(replacement.WorkspaceId, replacement);
            _workspaceByWindow[request.WindowId] = replacement.WorkspaceId;
            if (ownerClientId is { } registeringClientId)
            {
                _clientByWindow[request.WindowId] = registeringClientId;
            }

            var snapshot = replacement.Snapshot();
            return HostResult<WorkspaceGraphSnapshot>.Succeed(snapshot, snapshot.Revision);
        }
    }

    public HostResult<WorkspaceGraphSnapshot> Get(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(workspaceId, out var graph))
            {
                return NotFound();
            }

            var snapshot = graph.Snapshot();
            return HostResult<WorkspaceGraphSnapshot>.Succeed(snapshot, snapshot.Revision);
        }
    }

    public HostResult<Unit> Unregister(
        UnregisterWorkspaceGraphRequest request,
        ClientId? ownerClientId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(request.WorkspaceId, out var graph)
                || graph.WindowId != request.WindowId)
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(HostErrorCode.NotFound, "The workspace graph was not found."),
                    0);
            }

            var currentRevision = graph.Revision;
            if (ownerClientId is { } clientId
                && _clientByWindow.TryGetValue(request.WindowId, out var currentClientId)
                && currentClientId != clientId)
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The window runtime graph is owned by another client."),
                    currentRevision);
            }

            if (expectedRevision is { } expected && expected != currentRevision)
            {
                return RevisionConflict<Unit>(currentRevision, expected);
            }

            _workspaceByWindow.Remove(request.WindowId);
            _clientByWindow.Remove(request.WindowId);
            _workspaces.Remove(request.WorkspaceId);
            var removedRevision = graph.Remove();
            return HostResult<Unit>.Succeed(Unit.Value, removedRevision);
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivateTab(
        ActivateWorkspaceTabRequest request,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return _workspaces.TryGetValue(request.WorkspaceId, out var graph)
                ? graph.ActivateTab(request.TabId, expectedRevision)
                : NotFound();
        }
    }

    public HostResult<WorkspaceGraphSnapshot> ActivatePanel(
        ActivateWorkspacePanelRequest request,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return _workspaces.TryGetValue(request.WorkspaceId, out var graph)
                ? graph.ActivatePanel(request.TabId, request.PanelId, expectedRevision)
                : NotFound();
        }
    }

    public HostResult<WorkspaceGraphSnapshot>? ValidateSessionOwner(
        SessionOwner owner,
        PanelKind kind)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!TryGetByWindowUnsafe(owner.WindowId, out var graph))
            {
                return _workspaces.TryGetValue(owner.WorkspaceId, out var graphForWorkspace)
                    ? InvalidSessionOwner(
                        graphForWorkspace.Revision,
                        "The session owner workspace belongs to another window.")
                    : null;
            }

            if (graph.WorkspaceId != owner.WorkspaceId)
            {
                return InvalidSessionOwner(
                    graph.Revision,
                    "The session owner workspace is not active in the owner window.");
            }

            return graph.ValidateSessionOwner(owner.TabId, owner.PanelId, kind);
        }
    }

    public HostResult<WorkspaceGraphSnapshot>? LinkSession(
        SessionOwner owner,
        PanelKind kind,
        SessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!TryGetByWindowUnsafe(owner.WindowId, out var graph))
            {
                return _workspaces.TryGetValue(owner.WorkspaceId, out var graphForWorkspace)
                    ? InvalidSessionOwner(
                        graphForWorkspace.Revision,
                        "The session owner workspace belongs to another window.")
                    : null;
            }

            if (graph.WorkspaceId != owner.WorkspaceId)
            {
                return InvalidSessionOwner(
                    graph.Revision,
                    "The session owner workspace is not active in the owner window.");
            }

            return graph.LinkSession(owner.TabId, owner.PanelId, kind, sessionId);
        }
    }

    public void UnlinkSession(
        SessionOwner owner,
        PanelKind kind,
        SessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!TryGetByWindowUnsafe(owner.WindowId, out var graph)
                || graph.WorkspaceId != owner.WorkspaceId)
            {
                return;
            }

            graph.UnlinkSession(owner.TabId, owner.PanelId, kind, sessionId);
        }
    }

    public bool TryGetWatchSource(
        WorkspaceInstanceId workspaceId,
        out HostedWorkspaceGraph graph)
    {
        lock (_gate)
        {
            return _workspaces.TryGetValue(workspaceId, out graph!);
        }
    }

    public void RemoveWindow(WindowInstanceId windowId)
    {
        lock (_gate)
        {
            if (!TryGetByWindowUnsafe(windowId, out var graph))
            {
                return;
            }

            _workspaceByWindow.Remove(windowId);
            _clientByWindow.Remove(windowId);
            _workspaces.Remove(graph.WorkspaceId);
            graph.Remove();
        }
    }

    public void RemoveClient(ClientId clientId)
    {
        HostedWorkspaceGraph[] graphs;
        lock (_gate)
        {
            var windows = _clientByWindow
                .Where(pair => pair.Value == clientId)
                .Select(pair => pair.Key)
                .ToArray();
            var removed = new List<HostedWorkspaceGraph>(windows.Length);
            foreach (var windowId in windows)
            {
                _clientByWindow.Remove(windowId);
                if (!_workspaceByWindow.Remove(windowId, out var workspaceId)
                    || !_workspaces.Remove(workspaceId, out var graph))
                {
                    continue;
                }

                removed.Add(graph);
            }

            graphs = removed.ToArray();
        }

        foreach (var graph in graphs)
        {
            graph.Remove();
        }
    }

    public void Dispose()
    {
        HostedWorkspaceGraph[] graphs;
        lock (_gate)
        {
            graphs = _workspaces.Values.ToArray();
            _clientByWindow.Clear();
            _workspaceByWindow.Clear();
            _workspaces.Clear();
        }

        foreach (var graph in graphs)
        {
            graph.Remove();
        }
    }

    private bool TryGetByWindowUnsafe(
        WindowInstanceId windowId,
        out HostedWorkspaceGraph graph)
    {
        if (_workspaceByWindow.TryGetValue(windowId, out var workspaceId)
            && _workspaces.TryGetValue(workspaceId, out graph!))
        {
            return true;
        }

        graph = null!;
        return false;
    }

    private static bool TryReconcileSessionLinks(
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        IReadOnlyList<SessionDescriptor> liveSessions,
        WorkspaceInstance? currentWorkspace,
        out WorkspaceInstance reconciled,
        out HostError? error)
    {
        var sessionsByPanel = new Dictionary<PanelInstanceId, List<SessionDescriptor>>();
        foreach (var session in liveSessions.Where(session =>
                     session.Owner.WindowId == windowId
                     || session.Owner.WorkspaceId == workspace.Id))
        {
            if (session.Owner.WindowId != windowId
                || session.Owner.WorkspaceId != workspace.Id)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session owner does not match the registered window and workspace.");
                return false;
            }

            var tab = workspace.Tabs.SingleOrDefault(candidate =>
                candidate.Id == session.Owner.TabId);
            var panel = tab?.Panels.SingleOrDefault(candidate =>
                candidate.Id == session.Owner.PanelId);
            if (tab is null || panel is null)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session owner does not identify a panel in the workspace graph.");
                return false;
            }

            if (panel.Kind != session.Kind)
            {
                reconciled = workspace;
                error = HostError.Create(
                    HostErrorCode.InvalidRequest,
                    "A live session kind does not match its owner panel kind.");
                return false;
            }

            if (!sessionsByPanel.TryGetValue(panel.Id, out var panelSessions))
            {
                panelSessions = [];
                sessionsByPanel.Add(panel.Id, panelSessions);
            }

            panelSessions.Add(session);
        }

        var currentPanels = currentWorkspace?.Tabs
            .SelectMany(tab => tab.Panels)
            .ToDictionary(panel => panel.Id);
        reconciled = workspace;
        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                SessionId? sessionId = null;
                sessionsByPanel.TryGetValue(panel.Id, out var panelSessions);
                if (currentPanels is not null
                    && currentPanels.TryGetValue(panel.Id, out var currentPanel))
                {
                    sessionId = currentPanel.SessionId is { } currentSessionId
                        && panelSessions?.Any(candidate => candidate.Id == currentSessionId) == true
                            ? currentSessionId
                            : null;
                }
                else if (panelSessions is not null)
                {
                    if (panelSessions.Count == 1)
                    {
                        sessionId = panelSessions[0].Id;
                    }
                    else
                    {
                        reconciled = workspace;
                        error = HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "More than one live session claims the same workspace panel without an authoritative current link.");
                        return false;
                    }
                }

                reconciled = reconciled.ReplacePanelSession(tab.Id, panel.Id, sessionId);
            }
        }

        error = null;
        return true;
    }

    private static HostResult<WorkspaceGraphSnapshot> InvalidSessionOwner(
        long revision,
        string message) =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<WorkspaceGraphSnapshot> NotFound() =>
        HostResult<WorkspaceGraphSnapshot>.Fail(
            HostError.Create(HostErrorCode.NotFound, "The workspace graph was not found."),
            0);

    private static HostResult<T> RevisionConflict<T>(
        long currentRevision,
        long expectedRevision) =>
        HostResult<T>.Fail(
            HostError.Create(
                HostErrorCode.RevisionConflict,
                $"Expected revision {expectedRevision}, but the current revision is {currentRevision}."),
            currentRevision);
}
