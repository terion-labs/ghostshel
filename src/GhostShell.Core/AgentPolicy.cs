using System.Collections.Immutable;

namespace GhostShell.Core;

public enum AgentCapability
{
    /// <summary>
    /// Bounded terminal screen, cursor, mode, and shell-integration reads.
    /// </summary>
    TerminalRead,

    /// <summary>
    /// Exact text, key, paste, mouse, and interrupt input to a terminal.
    /// </summary>
    RunCommands,

    /// <summary>
    /// File creation, replacement, rename, move, and deletion.
    /// </summary>
    EditFiles,

    ReadFiles,
    Search,

    /// <summary>
    /// Git operations that change a worktree, index, refs, or remotes.
    /// </summary>
    Git,

    WebFetch,

    /// <summary>
    /// Docker engine, container lifecycle, and exec control.
    /// </summary>
    Docker,

    DestructiveTerminalActions,
    BrowserNavigation,
    BrowserData,
    ProcessControl,
    McpTools,
    SecretUse,
    BrowserInteraction,
}

public enum AgentPermission
{
    Off,
    Ask,
    Auto,
    Yolo,
}

public sealed record AgentModelSelection(string Provider, string Model)
{
    public bool IsStructurallyValid() =>
        AgentPolicy.IsValidProvider(Provider)
        && AgentPolicy.IsValidModel(Model);
}

public sealed record AgentPolicy(
    string Provider,
    string Model,
    ImmutableDictionary<AgentCapability, AgentPermission> Permissions)
{
    public const int MaximumSystemPromptLength = 8_000;
    public const int MaximumProviderLength = 256;
    public const int MaximumModelLength = 256;

    private static readonly ImmutableHashSet<AgentCapability> LegacyCapabilities =
        ImmutableHashSet.Create(
            AgentCapability.RunCommands,
            AgentCapability.EditFiles,
            AgentCapability.ReadFiles,
            AgentCapability.Search,
            AgentCapability.Git,
            AgentCapability.WebFetch,
            AgentCapability.Docker);

    private static readonly ImmutableHashSet<AgentCapability> PreviousFullCapabilities =
        ImmutableHashSet.Create(
            AgentCapability.TerminalRead,
            AgentCapability.RunCommands,
            AgentCapability.EditFiles,
            AgentCapability.ReadFiles,
            AgentCapability.Search,
            AgentCapability.Git,
            AgentCapability.WebFetch,
            AgentCapability.Docker,
            AgentCapability.DestructiveTerminalActions,
            AgentCapability.BrowserNavigation,
            AgentCapability.BrowserData,
            AgentCapability.ProcessControl,
            AgentCapability.McpTools,
            AgentCapability.SecretUse);

    public static ImmutableArray<AgentCapability> Capabilities { get; } =
        Enum.GetValues<AgentCapability>().ToImmutableArray();

    public static AgentPolicy Default { get; } = new(
        "Anthropic",
        "claude-opus-4.8",
        new Dictionary<AgentCapability, AgentPermission>
        {
            [AgentCapability.TerminalRead] = AgentPermission.Auto,
            [AgentCapability.RunCommands] = AgentPermission.Ask,
            [AgentCapability.EditFiles] = AgentPermission.Ask,
            [AgentCapability.ReadFiles] = AgentPermission.Auto,
            [AgentCapability.Search] = AgentPermission.Auto,
            [AgentCapability.Git] = AgentPermission.Ask,
            [AgentCapability.WebFetch] = AgentPermission.Ask,
            [AgentCapability.Docker] = AgentPermission.Off,
            [AgentCapability.DestructiveTerminalActions] = AgentPermission.Ask,
            [AgentCapability.BrowserNavigation] = AgentPermission.Ask,
            [AgentCapability.BrowserData] = AgentPermission.Ask,
            [AgentCapability.BrowserInteraction] = AgentPermission.Ask,
            [AgentCapability.ProcessControl] = AgentPermission.Off,
            [AgentCapability.McpTools] = AgentPermission.Off,
            [AgentCapability.SecretUse] = AgentPermission.Ask,
        }.ToImmutableDictionary());

    /// <summary>
    /// Optional summarization route. Null means inherit the next broader layer;
    /// a fully resolved policy falls back to the global primary model.
    /// </summary>
    public AgentModelSelection? CompactionModel { get; init; }

    /// <summary>
    /// Optional conversation-title route. Null inherits an explicitly configured
    /// broader route; resolved legacy policies fall back to their primary model.
    /// </summary>
    public AgentModelSelection? TitleModel { get; init; }

    /// <summary>
    /// Optional user-authored instructions appended to the invariant runtime
    /// safety prompt. Null inherits the next broader policy layer.
    /// </summary>
    public string? SystemPrompt { get; init; }

    public string EffectiveSummary =>
        $"Commands: {Format(GetPermission(AgentCapability.RunCommands))} · " +
        $"Files: {Format(GetPermission(AgentCapability.EditFiles))} · " +
        $"Git: {Format(GetPermission(AgentCapability.Git))} · " +
        $"Docker: {Format(GetPermission(AgentCapability.Docker))}";

    /// <summary>
    /// Returns a fail-closed permission for execution. This also lets definitions
    /// written before the precise desktop-v1 capabilities were added remain
    /// readable without granting those new capabilities.
    /// </summary>
    public AgentPermission GetPermission(AgentCapability capability)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        return Permissions is not null
            && Permissions.TryGetValue(capability, out var permission)
            && Enum.IsDefined(permission)
                ? permission
                : AgentPermission.Off;
    }

    public bool IsStructurallyValid()
    {
        if (!IsValidProvider(Provider)
            || !IsValidModel(Model)
            || CompactionModel is not null && !CompactionModel.IsStructurallyValid()
            || TitleModel is not null && !TitleModel.IsStructurallyValid()
            || SystemPrompt is not null && !IsValidSystemPrompt(SystemPrompt)
            || Permissions is null
            || Permissions.Keys.Any(capability => !Enum.IsDefined(capability))
            || Permissions.Values.Any(permission => !Enum.IsDefined(permission)))
        {
            return false;
        }

        var keys = Permissions.Keys.ToImmutableHashSet();
        return keys.SetEquals(Capabilities)
            || keys.SetEquals(PreviousFullCapabilities)
            || keys.SetEquals(LegacyCapabilities);
    }

    /// <summary>
    /// Durable policies are baseline configuration. YOLO is granted only as a
    /// separately confirmed, scoped, and expiring run-local overlay.
    /// </summary>
    public bool IsValidForDurableStorage() =>
        IsStructurallyValid()
        && Permissions.Values.All(permission => permission != AgentPermission.Yolo);

    public static bool IsValidProvider(string? value) =>
        IsValidIdentityValue(value, MaximumProviderLength);

    public static bool IsValidModel(string? value) =>
        IsValidIdentityValue(value, MaximumModelLength);

    public static bool IsValidSystemPrompt(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumSystemPromptLength
        && value.All(character =>
            !char.IsControl(character)
            || character is '\r' or '\n' or '\t');

    private static string Format(AgentPermission permission) =>
        permission == AgentPermission.Yolo ? "YOLO" : permission.ToString();

    private static bool IsValidIdentityValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}
