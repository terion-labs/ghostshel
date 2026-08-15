using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

internal static class RuntimeWorkspaceRecoveryCodec
{
    public const string SnapshotKey = "desktop.main-window";
    public const int SchemaVersion = 6;

    private const int OldestSupportedSchemaVersion = 4;

    private const int MaximumTabs = WorkspaceInstance.MaximumPanelCount;
    private const int MaximumPanelsPerTab = WorkspaceInstance.MaximumPanelCount;
    private const int MaximumGridDimension = 64;

    public static string Serialize(
        RuntimeWorkspaceViewModel? workspace,
        RuntimeHistorySource? historySource = null)
    {
        var payload = new RuntimeWindowRecoveryPayload(
            workspace is null || workspace.Tabs.Count == 0
                ? null
                : CaptureWorkspace(workspace, historySource));
        if (!TryValidate(
                payload,
                allowLegacyPolicyFallback: false,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        return JsonSerializer.Serialize(
            payload,
            RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload);
    }

    public static bool TryDeserialize(
        RuntimeRecoverySnapshot snapshot,
        out RuntimeWindowRecoveryPayload? payload,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        payload = null;
        if (snapshot.SchemaVersion is < OldestSupportedSchemaVersion or > SchemaVersion)
        {
            error = $"Runtime recovery schema {snapshot.SchemaVersion} is not supported.";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize(
                snapshot.PayloadJson,
                RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload);
        }
        catch (JsonException)
        {
            error = "Runtime recovery data is not valid JSON.";
            return false;
        }
        catch (NotSupportedException)
        {
            error = "Runtime recovery data uses an unsupported shape.";
            return false;
        }

        if (snapshot.SchemaVersion == 1
            && payload?.Workspace?.Tabs is { } versionOneTabs
            && versionOneTabs.Any(tab => tab?.HistorySource is not null))
        {
            payload = null;
            error = "Runtime recovery schema 1 cannot contain history-source metadata.";
            return false;
        }

        if (snapshot.SchemaVersion < 4
            && payload?.Workspace is { } olderWorkspace
            && (olderWorkspace.AgentPolicy is not null
                || olderWorkspace.Tabs?.Any(tab => tab?.AgentPolicy is not null) == true))
        {
            payload = null;
            error = "Older runtime recovery schemas cannot contain agent-policy provenance.";
            return false;
        }

        // Schema 5 accidentally persisted the effective global value as if it
        // were a workspace override. Keeping it would pin an inherited
        // workspace to whatever the global preference happened to be when the
        // snapshot was written. The durable workspace definition is consulted
        // during restore, so discarding this ambiguous value is lossless there.
        if (snapshot.SchemaVersion == 5 && payload?.Workspace is { } versionFiveWorkspace)
        {
            payload = payload with
            {
                Workspace = versionFiveWorkspace with { TerminalMultiplexingMode = null },
            };
        }

        if (snapshot.SchemaVersion >= 4
            && payload?.Workspace is { } currentWorkspace
            && (currentWorkspace.AgentPolicy is null
                || currentWorkspace.Tabs is null
                || currentWorkspace.Tabs.Any(tab => tab?.AgentPolicy is null)))
        {
            payload = null;
            error = "Runtime recovery schema 4 requires complete agent-policy provenance.";
            return false;
        }

        error = null;
        if (payload is null
            || !TryValidate(
                payload,
                snapshot.SchemaVersion < 4,
                out error))
        {
            payload = null;
            error ??= "Runtime recovery data is incomplete.";
            return false;
        }

        return true;
    }

    private static RuntimeWorkspaceRecoveryPayload CaptureWorkspace(
        RuntimeWorkspaceViewModel workspace,
        RuntimeHistorySource? historySource) =>
        new(
            workspace.Name,
            workspace.Accent,
            workspace.ActiveTab?.Id.Value,
            workspace.Connections
                .Select(connection => connection.Id.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RuntimeAgentPolicyRecoveryPayload.Capture(workspace.AgentPolicy),
            workspace.Tabs.Select(CaptureTab).ToArray(),
            historySource is null
                ? null
                : RuntimeHistorySourceRecoveryPayload.Capture(historySource),
            workspace.TerminalMultiplexingMode);

    private static RuntimeTabRecoveryPayload CaptureTab(RuntimeTabViewModel tab) =>
        new(
            tab.Id.Value,
            tab.Title,
            tab.Source,
            tab.ActivePanelId?.Value,
            tab.ZoomedPanelId?.Value,
            tab.UsesAutomaticLayout,
            tab.SerializeDockLayout(),
            tab.Columns,
            tab.Rows,
            tab.HistorySource is { } historySource
                ? RuntimeHistorySourceRecoveryPayload.Capture(historySource)
                : null,
            RuntimeAgentPolicyRecoveryPayload.Capture(tab.AgentPolicy),
            tab.Panels.Select(CapturePanel).ToArray(),
            tab.Icon,
            tab.HasChosenTitle,
            tab.HasChosenIcon);

    private static RuntimePanelRecoveryPayload CapturePanel(RuntimePanelViewModel panel)
    {
        var kind = panel switch
        {
            TerminalRuntimePanelViewModel => RuntimePanelRecoveryKind.Terminal,
            BrowserRuntimePanelViewModel => RuntimePanelRecoveryKind.Browser,
            FileRuntimePanelViewModel => RuntimePanelRecoveryKind.FileViewer,
            StatisticsRuntimePanelViewModel => RuntimePanelRecoveryKind.Statistics,
            ProcessMonitorRuntimePanelViewModel => RuntimePanelRecoveryKind.ProcessMonitor,
            DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel =>
                RuntimePanelRecoveryKind.DatabaseViewer,
            DockerRuntimePanelViewModel => RuntimePanelRecoveryKind.Docker,
            PanelPlaceholderViewModel => RuntimePanelRecoveryKind.Placeholder,
            _ => RuntimePanelRecoveryKind.Unavailable,
        };
        var terminal = panel as TerminalRuntimePanelViewModel;
        var browser = panel as BrowserRuntimePanelViewModel;
        var database = panel as DatabaseRuntimePanelViewModel;
        var redis = panel as RedisRuntimePanelViewModel;
        var file = panel as FileRuntimePanelViewModel;
        var statistics = panel as StatisticsRuntimePanelViewModel;
        var processes = panel as ProcessMonitorRuntimePanelViewModel;
        var docker = panel as DockerRuntimePanelViewModel;
        return new RuntimePanelRecoveryPayload(
            panel.Id.Value,
            kind,
            panel.Title,
            kind == RuntimePanelRecoveryKind.Unavailable ? panel.KindLabel : null,
            terminal?.ConnectionId.Value
                ?? browser?.ConnectionId.Value
                ?? file?.ConnectionId.Value
                ?? statistics?.ConnectionId.Value
                ?? processes?.ConnectionId.Value
                ?? docker?.ConnectionId.Value
                ?? database?.TunnelConnectionId?.Value
                ?? redis?.TunnelConnectionId?.Value,
            terminal?.RecoveryStartupLocation
                ?? browser?.CurrentAddress.ToString()
                ?? database?.RecoveryTarget
                ?? redis?.RecoveryTarget,
            file?.SelectedProfile?.Id ?? file?.CurrentLocation?.ProviderProfileId,
            file?.CurrentLocation is { } location
                ? RuntimeFileLocationRecoveryPayload.Capture(location)
                : null,
            file?.ShowHidden ?? false,
            file?.Filter,
            panel.LayoutColumn,
            panel.LayoutRow,
            panel.LayoutColumnSpan,
            panel.LayoutRowSpan,
            panel.LayoutMinimumWidth,
            panel.LayoutMinimumHeight,
            terminal?.MultiplexerSession is { } multiplexer
                ? new RuntimeTerminalMultiplexerRecoveryPayload(
                    multiplexer.Mode,
                    multiplexer.SessionName,
                    multiplexer.IsEstablished)
                : null);
    }

    private static bool TryValidate(
        RuntimeWindowRecoveryPayload payload,
        bool allowLegacyPolicyFallback,
        out string? error)
    {
        if (payload.Workspace is not { } workspace)
        {
            error = null;
            return true;
        }

        if (!IsDisplayText(workspace.Name, 256)
            || !IsDisplayText(workspace.Accent, 64)
            || workspace.ConnectionIds is null
            || workspace.ConnectionIds.Length > 512
            || workspace.ConnectionIds.Any(id => !IsIdentifier(id))
            || workspace.ConnectionIds.Distinct(StringComparer.Ordinal).Count()
                != workspace.ConnectionIds.Length
            || workspace.TerminalMultiplexingMode is { } terminalMultiplexingMode
                && !Enum.IsDefined(terminalMultiplexingMode)
            || workspace.AgentPolicy is { } workspacePolicy
                && (!workspacePolicy.TryValidate()
                    || workspacePolicy.Sources.Any(source =>
                        source.Kind == ScreenDefinition.Kind.Value))
            || workspace.Tabs is null
            || workspace.Tabs.Length is 0 or > MaximumTabs)
        {
            error = "Runtime workspace recovery metadata is invalid.";
            return false;
        }

        error = null;
        var tabKeys = new HashSet<string>(StringComparer.Ordinal);
        var panelCount = 0;
        foreach (var tab in workspace.Tabs)
        {
            if (tab is null
                || !tabKeys.Add(tab.Key)
                || !TryValidate(
                    tab,
                    workspace.AgentPolicy,
                    allowLegacyPolicyFallback,
                    out error))
            {
                error ??= "Runtime tab recovery metadata is invalid.";
                return false;
            }

            panelCount += tab.Panels!.Length;
            if (panelCount > WorkspaceInstance.MaximumPanelCount)
            {
                error = $"A recovered workspace cannot contain more than {WorkspaceInstance.MaximumPanelCount} panels.";
                return false;
            }
        }

        if (workspace.ActiveTabKey is not null && !tabKeys.Contains(workspace.ActiveTabKey))
        {
            error = "The recovered active tab does not exist.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidate(
        RuntimeTabRecoveryPayload tab,
        RuntimeAgentPolicyRecoveryPayload? workspacePolicy,
        bool allowLegacyPolicyFallback,
        out string? error)
    {
        if (!IsIdentifier(tab.Key)
            || !IsDisplayText(tab.Title, 256)
            || !IsDisplayText(tab.Source, 128)
            || tab.Icon is { } icon && !IsDisplayText(icon, 64)
            || tab.HistorySource is { } historySource
                && !TryValidate(historySource)
            || tab.AgentPolicy is { } tabPolicy
                && !tabPolicy.TryValidate()
            || string.IsNullOrWhiteSpace(tab.DockLayoutJson)
            || tab.DockLayoutJson.Length > 4 * 1024 * 1024
            || tab.DockLayoutJson.Contains('\0')
            || tab.Columns is < 1 or > MaximumGridDimension
            || tab.Rows is < 1 or > MaximumGridDimension
            || tab.Panels is null
            || tab.Panels.Length is 0 or > MaximumPanelsPerTab)
        {
            error = "Runtime tab recovery metadata is invalid.";
            return false;
        }

        var screenPolicySource = tab.AgentPolicy?.Sources
            .SingleOrDefault(source => source.Kind == ScreenDefinition.Kind.Value);
        var workspacePolicySource = workspacePolicy?.Sources
            .SingleOrDefault(source => source.Kind == WorkspaceDefinition.Kind.Value);
        var tabWorkspacePolicySource = tab.AgentPolicy?.Sources
            .SingleOrDefault(source => source.Kind == WorkspaceDefinition.Kind.Value);
        if (screenPolicySource is not null
            && (tab.HistorySource is null
                || tab.HistorySource.SourceKind != screenPolicySource.Kind
                || tab.HistorySource.SourceValue != screenPolicySource.Value))
        {
            error = "Recovered screen policy provenance does not match its history source.";
            return false;
        }

        if (tab.HistorySource?.SourceKind == ScreenDefinition.Kind.Value
            && screenPolicySource is null
            && !(allowLegacyPolicyFallback && tab.AgentPolicy is null)
            && tab.AgentPolicy?.IsLegacyFallback != true)
        {
            error = "Recovered saved-screen tab is missing its policy provenance.";
            return false;
        }

        if (workspacePolicy is not null
            && tab.AgentPolicy is not null
            && (screenPolicySource is null
                && tab.AgentPolicy.IsLegacyFallback != workspacePolicy.IsLegacyFallback
                || screenPolicySource is null
                && tab.AgentPolicy.HasPolicyOverride != workspacePolicy.HasPolicyOverride
                || tabWorkspacePolicySource != workspacePolicySource
                || screenPolicySource is null
                && !tab.AgentPolicy.HasSamePolicyAs(workspacePolicy)))
        {
            error = "Recovered tab policy does not match its workspace policy lineage.";
            return false;
        }

        error = null;
        var panelKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in tab.Panels)
        {
            if (panel is null
                || !panelKeys.Add(panel.Key)
                || !TryValidate(panel, tab.Columns, tab.Rows, out error))
            {
                error ??= "Runtime panel recovery metadata is invalid.";
                return false;
            }
        }

        if (tab.ActivePanelKey is not null && !panelKeys.Contains(tab.ActivePanelKey))
        {
            error = "The recovered active panel does not exist.";
            return false;
        }

        if (tab.ZoomedPanelKey is not null
            && (!panelKeys.Contains(tab.ZoomedPanelKey)
                || tab.ZoomedPanelKey != tab.ActivePanelKey))
        {
            error = "The recovered zoomed panel does not match the active panel.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidate(RuntimeHistorySourceRecoveryPayload source) =>
        IsIdentifier(source.SourceKind)
        && IsIdentifier(source.SourceValue)
        && IsDisplayText(source.DurableTitle, 256);

    private static bool TryValidate(
        RuntimePanelRecoveryPayload panel,
        int columns,
        int rows,
        out string? error)
    {
        var validKindData = panel.Kind switch
        {
            RuntimePanelRecoveryKind.Terminal =>
                IsIdentifier(panel.ConnectionId)
                && IsOptionalText(panel.StartupLocation, 4_096)
                && panel.FileLocation is null
                && (panel.Multiplexer is null
                    || panel.Multiplexer.Mode == TerminalMultiplexingMode.Automatic
                    && TerminalMultiplexerSession.IsValidSessionName(
                        panel.Multiplexer.SessionName)),
            RuntimePanelRecoveryKind.FileViewer =>
                panel.Multiplexer is null
                && IsOptionalIdentifier(panel.FileProviderProfileId)
                && IsOptionalIdentifier(panel.ConnectionId)
                && (panel.FileLocation is null
                    || panel.FileLocation.TryValidate(panel.FileProviderProfileId, out _))
                && IsOptionalText(panel.Filter, 1_024),
            RuntimePanelRecoveryKind.Browser =>
                panel.Multiplexer is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && panel.StartupLocation is { } browserAddress
                && BrowserAddress.TryParse(browserAddress, out _)
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Unavailable =>
                panel.Multiplexer is null
                && IsDisplayText(panel.KindLabel, 128)
                && panel.ConnectionId is null
                && panel.FileLocation is null,
            RuntimePanelRecoveryKind.Placeholder =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && panel.ConnectionId is null
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Statistics or RuntimePanelRecoveryKind.ProcessMonitor =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.DatabaseViewer =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && IsOptionalText(panel.StartupLocation, 4_096)
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Docker =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsIdentifier(panel.ConnectionId)
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            _ => false,
        };
        if (!IsIdentifier(panel.Key)
            || !IsDisplayText(panel.Title, 256)
            || !validKindData
            || panel.Column < 0
            || panel.Row < 0
            || panel.ColumnSpan <= 0
            || panel.RowSpan <= 0
            || panel.Column + panel.ColumnSpan > columns
            || panel.Row + panel.RowSpan > rows
            || panel.MinimumWidth is < 1 or > 100_000
            || panel.MinimumHeight is < 1 or > 100_000)
        {
            error = "Runtime panel recovery metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static bool IsOptionalIdentifier(string? value) =>
        value is null || IsIdentifier(value);

    private static bool IsDisplayText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Contains('\0');

    private static bool IsOptionalText(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength && !value.Contains('\0');
}

internal sealed record RuntimeWindowRecoveryPayload(RuntimeWorkspaceRecoveryPayload? Workspace);

/// <summary>
/// A restored workspace, and — since a restart must not sever it — which
/// definition it was opened from.
///
/// <see cref="HistorySource"/> is optional and last so a payload written before
/// it existed still reads. Without it a restored workspace was anonymous: the
/// shell could not tell that the tile you clicked was the workspace already
/// running, so it built a second one and the first workspace's terminals were
/// replaced by fresh shells.
/// </summary>
internal sealed record RuntimeWorkspaceRecoveryPayload(
    string Name,
    string Accent,
    string? ActiveTabKey,
    string[] ConnectionIds,
    RuntimeAgentPolicyRecoveryPayload? AgentPolicy,
    RuntimeTabRecoveryPayload[] Tabs,
    RuntimeHistorySourceRecoveryPayload? HistorySource = null,
    // Despite the historical property name, this stores only a concrete
    // workspace override. Null follows the current application preference.
    TerminalMultiplexingMode? TerminalMultiplexingMode = null);

internal sealed record RuntimeTabRecoveryPayload(
    string Key,
    string Title,
    string Source,
    string? ActivePanelKey,
    string? ZoomedPanelKey,
    bool UsesAutomaticLayout,
    string DockLayoutJson,
    int Columns,
    int Rows,
    RuntimeHistorySourceRecoveryPayload? HistorySource,
    RuntimeAgentPolicyRecoveryPayload? AgentPolicy,
    RuntimePanelRecoveryPayload[] Panels,
    string? Icon = null,
    bool? HasChosenTitle = null,
    bool? HasChosenIcon = null);

internal sealed record RuntimeAgentPolicyRecoveryPayload(
    string Provider,
    string Model,
    Dictionary<AgentCapability, AgentPermission> Permissions,
    RuntimeAgentPolicySourceRecoveryPayload[] Sources,
    bool IsLegacyFallback,
    bool HasPolicyOverride,
    AgentModelSelection? CompactionModel = null,
    AgentModelSelection? TitleModel = null,
    string? SystemPrompt = null)
{
    public static RuntimeAgentPolicyRecoveryPayload Capture(
        RuntimeAgentPolicyProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return new(
            provenance.EffectivePolicy.Provider,
            provenance.EffectivePolicy.Model,
            provenance.EffectivePolicy.Permissions.ToDictionary(
                item => item.Key,
                item => item.Value),
            provenance.Sources
                .Select(RuntimeAgentPolicySourceRecoveryPayload.Capture)
                .ToArray(),
            provenance.IsLegacyFallback,
            provenance.HasPolicyOverride,
            provenance.EffectivePolicy.CompactionModel,
            provenance.EffectivePolicy.TitleModel,
            provenance.EffectivePolicy.SystemPrompt);
    }

    public bool TryValidate()
    {
        if (Permissions is null
            || Sources is null
            || Sources.Length > 2
            || Sources.Any(source => source is null || !source.TryValidate())
            || Sources
                .GroupBy(source => source.Kind, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            return false;
        }

        var policy = new AgentPolicy(
            Provider,
            Model,
            Permissions.ToImmutableDictionary())
        {
            CompactionModel = CompactionModel,
            TitleModel = TitleModel,
            SystemPrompt = SystemPrompt,
        };
        return policy.IsValidForDurableStorage()
            && (!IsLegacyFallback
                || Sources.Length == 0
                && !HasPolicyOverride
                && HasSamePolicyAs(AgentPolicy.Default))
            && (!HasPolicyOverride
                ? HasSamePolicyAs(AgentPolicy.Default)
                : Sources.Length > 0)
            && Permissions.Keys.ToHashSet().SetEquals(AgentPolicy.Capabilities);
    }

    public RuntimeAgentPolicyProvenance ToProvenance()
    {
        if (!TryValidate())
        {
            throw new InvalidOperationException(
                "Recovered agent-policy provenance is invalid.");
        }

        return new(
            new AgentPolicy(
                Provider,
                Model,
                Permissions.ToImmutableDictionary())
            {
                CompactionModel = CompactionModel,
                TitleModel = TitleModel,
                SystemPrompt = SystemPrompt,
            },
            Sources.Select(source => source.ToSource()),
            IsLegacyFallback,
            HasPolicyOverride);
    }

    public bool HasSamePolicyAs(RuntimeAgentPolicyRecoveryPayload other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Provider, other.Provider, StringComparison.Ordinal)
            && string.Equals(Model, other.Model, StringComparison.Ordinal)
            && CompactionModel == other.CompactionModel
            && TitleModel == other.TitleModel
            && string.Equals(SystemPrompt, other.SystemPrompt, StringComparison.Ordinal)
            && Permissions.Count == other.Permissions.Count
            && Permissions.All(item =>
                other.Permissions.TryGetValue(item.Key, out var permission)
                && item.Value == permission);
    }

    private bool HasSamePolicyAs(AgentPolicy other)
    {
        var resolved = AgentPolicyResolver.Resolve(other);
        return string.Equals(Provider, resolved.Provider, StringComparison.Ordinal)
        && string.Equals(Model, resolved.Model, StringComparison.Ordinal)
        && CompactionModel == resolved.CompactionModel
        && TitleModel == resolved.TitleModel
        && string.Equals(SystemPrompt, resolved.SystemPrompt, StringComparison.Ordinal)
        && AgentPolicy.Capabilities.All(capability =>
            Permissions.TryGetValue(capability, out var permission)
            && permission == resolved.GetPermission(capability));
    }
}

internal sealed record RuntimeAgentPolicySourceRecoveryPayload(
    string Kind,
    string Value,
    long Revision)
{
    public static RuntimeAgentPolicySourceRecoveryPayload Capture(
        RuntimeAgentPolicyProvenance.Source source) =>
        new(source.Definition.Kind.Value, source.Definition.Value, source.Revision);

    public bool TryValidate() =>
        IsIdentifier(Kind)
        && IsIdentifier(Value)
        && Revision > 0
        && Kind is "screen" or "workspace";

    public RuntimeAgentPolicyProvenance.Source ToSource() =>
        new(
            new DefinitionKey(new DefinitionKind(Kind), Value),
            Revision);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);
}

internal sealed record RuntimeHistorySourceRecoveryPayload(
    string SourceKind,
    string SourceValue,
    string DurableTitle)
{
    public static RuntimeHistorySourceRecoveryPayload Capture(RuntimeHistorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(
            source.SourceDefinition.Kind.Value,
            source.SourceDefinition.Value,
            source.DurableTitle);
    }

    public RuntimeHistorySource ToHistorySource() =>
        new(
            new DefinitionKey(new DefinitionKind(SourceKind), SourceValue),
            DurableTitle);
}

internal enum RuntimePanelRecoveryKind
{
    Terminal = 0,
    FileViewer = 1,
    Unavailable = 2,
    Statistics = 3,
    ProcessMonitor = 4,
    Browser = 5,
    Placeholder = 6,
    DatabaseViewer = 7,
    Docker = 8,
}

internal sealed record RuntimePanelRecoveryPayload(
    string Key,
    RuntimePanelRecoveryKind Kind,
    string Title,
    string? KindLabel,
    string? ConnectionId,
    string? StartupLocation,
    string? FileProviderProfileId,
    RuntimeFileLocationRecoveryPayload? FileLocation,
    bool ShowHidden,
    string? Filter,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan,
    double MinimumWidth,
    double MinimumHeight,
    RuntimeTerminalMultiplexerRecoveryPayload? Multiplexer = null);

internal sealed record RuntimeTerminalMultiplexerRecoveryPayload(
    TerminalMultiplexingMode Mode,
    string SessionName,
    bool IsEstablished)
{
    public TerminalMultiplexerSession ToSession() =>
        new(Mode, SessionName, IsEstablished);
}

internal enum RuntimeFileAddressRecoveryKind
{
    Hierarchical,
    ObjectKey,
    ContainerRoot,
}

internal sealed record RuntimeFileLocationRecoveryPayload(
    string ProviderProfileId,
    string? Authority,
    RuntimeFileAddressRecoveryKind AddressKind,
    string[]? PathSegments,
    string? ObjectKey)
{
    public static RuntimeFileLocationRecoveryPayload Capture(FilePanelLocation location) =>
        location.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.Hierarchical,
                hierarchical.Path.Segments.Select(segment => segment.Value).ToArray(),
                null),
            FilePanelAddress.ObjectKey objectKey => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.ObjectKey,
                null,
                objectKey.Key),
            FilePanelAddress.ContainerRoot => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.ContainerRoot,
                null,
                null),
            _ => throw new InvalidOperationException("The file location address is not supported."),
        };

    public FilePanelLocation ToLocation()
    {
        FilePanelAddress address = AddressKind switch
        {
            RuntimeFileAddressRecoveryKind.Hierarchical => new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                    (PathSegments ?? []).Select(segment => new FilePanelPathSegment(segment)))),
            RuntimeFileAddressRecoveryKind.ObjectKey => new FilePanelAddress.ObjectKey(ObjectKey!),
            RuntimeFileAddressRecoveryKind.ContainerRoot => new FilePanelAddress.ContainerRoot(),
            _ => throw new InvalidOperationException("The recovered file address is not supported."),
        };
        return new FilePanelLocation(ProviderProfileId, Authority, address);
    }

    public bool TryValidate(string? expectedProfileId, out string? error)
    {
        if (!IsBounded(ProviderProfileId, 128)
            || expectedProfileId is not null && ProviderProfileId != expectedProfileId
            || Authority is { } authority
                && (!IsBounded(authority, 255)
                    || authority.Any(character => character is '/' or '\\')))
        {
            error = "The recovered file-provider identity is invalid.";
            return false;
        }

        var validAddress = AddressKind switch
        {
            RuntimeFileAddressRecoveryKind.Hierarchical =>
                ObjectKey is null
                && PathSegments is { Length: <= 1_024 }
                && PathSegments.All(segment =>
                    IsBounded(segment, 255)
                    && segment is not "." and not ".."
                    && !segment.Contains('/', StringComparison.Ordinal)),
            RuntimeFileAddressRecoveryKind.ObjectKey =>
                PathSegments is null && IsBounded(ObjectKey, 4_096),
            RuntimeFileAddressRecoveryKind.ContainerRoot =>
                PathSegments is null && ObjectKey is null,
            _ => false,
        };
        error = validAddress ? null : "The recovered file location is invalid.";
        return validAddress;
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= maximumLength
        && !value.Contains('\0');
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RuntimeWindowRecoveryPayload))]
internal sealed partial class RuntimeWorkspaceRecoveryJsonContext : JsonSerializerContext;
