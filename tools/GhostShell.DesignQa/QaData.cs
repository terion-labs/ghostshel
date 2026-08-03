using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.DesignQa;

/// <summary>
/// Deterministic presentation fixture that mirrors the density of the Pencil
/// reference frames. This is harness-only sample content: it is never written
/// to a store and never presented as the user's real connections or sessions.
/// </summary>
internal static class QaData
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 26, 9, 30, 0, TimeSpan.Zero);

    private static StoredDefinition<T> Stored<T>(T value)
        where T : IDurableDefinition =>
        new(value, 1, Now, Now);

    private static ConnectionProfile Connection(
        string id,
        string name,
        ConnectionEndpoint endpoint,
        SshHostKeyPolicy hostKeyPolicy,
        params string[] tags) =>
        new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            name,
            endpoint,
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            hostKeyPolicy,
            tags);

    public static IReadOnlyList<StoredDefinition<ConnectionProfile>> Connections { get; } =
    [
        Stored(Connection(
            "production-api",
            "production-api",
            new ConnectionEndpoint.Ssh("10.0.1.42", 22, "deploy"),
            SshHostKeyPolicy.AcceptNew,
            "production",
            "us-east")),
        Stored(Connection(
            "staging-web",
            "staging-web",
            new ConnectionEndpoint.Ssh("staging.ember.dev", 22, "dev"),
            SshHostKeyPolicy.AcceptNew,
            "staging")),
        Stored(Connection(
            "postgres-primary",
            "postgres-primary",
            new ConnectionEndpoint.Ssh("db.internal", 22, "admin"),
            SshHostKeyPolicy.AcceptNew,
            "database",
            "production")),
        Stored(Connection(
            "local-dev",
            "local-dev",
            new ConnectionEndpoint.Local("/bin/zsh"),
            SshHostKeyPolicy.NotApplicable,
            "local")),
        Stored(Connection(
            "redis-cache",
            "redis-cache",
            new ConnectionEndpoint.Docker("redis-cache", "ops"),
            SshHostKeyPolicy.NotApplicable,
            "cache",
            "staging")),
        Stored(Connection(
            "bastion-eu",
            "bastion-eu",
            new ConnectionEndpoint.Ssh("bastion.eu-west", 22, "jump"),
            SshHostKeyPolicy.AcceptNew,
            "production",
            "eu-west")),
        Stored(Connection(
            "ci-runner",
            "ci-runner",
            new ConnectionEndpoint.Wsl("Ubuntu", "runner"),
            SshHostKeyPolicy.NotApplicable,
            "ci")),
        Stored(Connection(
            "edge-proxy",
            "edge-proxy",
            new ConnectionEndpoint.Docker("edge-proxy", "admin"),
            SshHostKeyPolicy.NotApplicable,
            "edge",
            "production")),
    ];

    private static readonly LayoutSlotId SlotA = new("slot-a");
    private static readonly LayoutSlotId SlotB = new("slot-b");
    private static readonly LayoutSlotId SlotC = new("slot-c");
    private static readonly LayoutSlotId SlotD = new("slot-d");

    private static LayoutDefinition Layout(
        string id,
        string name,
        params LayoutSlotDefinition[] slots) =>
        new(new LayoutId(id), LayoutDefinition.CurrentSchemaVersion, name, new LayoutGrid(12, 8), slots);

    private static LayoutSlotDefinition Slot(
        LayoutSlotId id, int column, int row, int columnSpan, int rowSpan) =>
        new(id, new LayoutGridBounds(column, row, columnSpan, rowSpan), new LayoutMinimumSize(160, 90));

    public static IReadOnlyList<StoredDefinition<LayoutDefinition>> Layouts { get; } =
    [
        Stored(Layout(
            "split-three",
            "Split · three panels",
            Slot(SlotA, 0, 0, 6, 8),
            Slot(SlotB, 6, 0, 6, 4),
            Slot(SlotC, 6, 4, 6, 4))),
        Stored(Layout(
            "grid-four",
            "Grid · four panels",
            Slot(SlotA, 0, 0, 6, 4),
            Slot(SlotB, 6, 0, 6, 4),
            Slot(SlotC, 0, 4, 6, 4),
            Slot(SlotD, 6, 4, 6, 4))),
        Stored(Layout(
            "stacked-three",
            "Stacked · three panels",
            Slot(SlotA, 0, 0, 12, 4),
            Slot(SlotB, 0, 4, 6, 4),
            Slot(SlotC, 6, 4, 6, 4))),
        Stored(Layout(
            "split-two",
            "Split · two panels",
            Slot(SlotA, 0, 0, 6, 8),
            Slot(SlotB, 6, 0, 6, 8))),
    ];

    private static ScreenPanelDefinition Panel(
        string id, LayoutSlotId slot, string title, string connection) =>
        new(
            new ScreenPanelId(id),
            slot,
            ScreenPanelKind.Terminal,
            title,
            new ConnectionId(connection),
            PanelStartupBehavior.None);

    public static IReadOnlyList<StoredDefinition<ScreenDefinition>> Screens { get; } =
    [
        Stored(new ScreenDefinition(
            new ScreenId("deploy-dashboard"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deploy Dashboard",
            "Release watch for the production API and staging web tier.",
            new LayoutId("split-three"),
            [
                Panel("deploy-a", SlotA, "prod-api", "production-api"),
                Panel("deploy-b", SlotB, "staging-web", "staging-web"),
                Panel("deploy-c", SlotC, "logs", "production-api"),
            ],
            ["production"])),
        Stored(new ScreenDefinition(
            new ScreenId("full-stack-dev"),
            ScreenDefinition.CurrentSchemaVersion,
            "Full-stack Dev",
            "Local shell, database, and cache side by side.",
            new LayoutId("grid-four"),
            [
                Panel("dev-a", SlotA, "local-dev", "local-dev"),
                Panel("dev-b", SlotB, "postgres-primary", "postgres-primary"),
                Panel("dev-c", SlotC, "redis-cache", "redis-cache"),
                Panel("dev-d", SlotD, "edge-proxy", "edge-proxy"),
            ],
            ["local"])),
        Stored(new ScreenDefinition(
            new ScreenId("log-monitor"),
            ScreenDefinition.CurrentSchemaVersion,
            "Log Monitor",
            "Edge and CI log tails.",
            new LayoutId("stacked-three"),
            [
                Panel("log-a", SlotA, "edge-proxy", "edge-proxy"),
                Panel("log-b", SlotB, "ci-runner", "ci-runner"),
                Panel("log-c", SlotC, "bastion-eu", "bastion-eu"),
            ],
            ["observability"])),
        Stored(new ScreenDefinition(
            new ScreenId("infra-overview"),
            ScreenDefinition.CurrentSchemaVersion,
            "Infra Overview",
            "Bastion and cache overview.",
            new LayoutId("split-two"),
            [
                Panel("infra-a", SlotA, "bastion-eu", "bastion-eu"),
                Panel("infra-b", SlotB, "redis-cache", "redis-cache"),
            ],
            ["production"])),
    ];

    public static IReadOnlyList<StoredDefinition<WorkspaceDefinition>> Workspaces { get; } =
    [
        Stored(new WorkspaceDefinition(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            "Production release watch",
            "#B8793A",
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("ops-deploy"),
                    new ScreenId("deploy-dashboard"),
                    "Deploy Dashboard"),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("ops-logs"),
                    new ScreenId("log-monitor"),
                    "Log Monitor"),
            ],
            icon: "rocket")),
        Stored(new WorkspaceDefinition(
            new WorkspaceId("development"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Development",
            "Local stack and services",
            "#5FA97A",
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("dev-stack"),
                    new ScreenId("full-stack-dev"),
                    "Full-stack Dev"),
            ],
            icon: "code")),
    ];

    /// <summary>
    /// The same built-in terminal profile the catalog seeds on first run, so the
    /// terminal and keybinding routes render with the shipped defaults rather
    /// than empty controls.
    /// </summary>
    public static IReadOnlyList<StoredDefinition<TerminalProfile>> TerminalProfiles { get; } =
    [
        Stored(new TerminalProfile(
            new TerminalProfileId("builtin.terminal.default"),
            "Default terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.GhostShellDark,
            BuiltInKeymaps.MacOsTerminalId)),
    ];

    public static IReadOnlyList<StoredDefinition<KeymapProfile>> Keymaps { get; } =
        BuiltInKeymaps.All.Select(Stored).ToArray();

    /// <summary>
    /// The capture set exercises a side-docked tab strip so the vertical layout
    /// is reviewable, not only the default top strip.
    /// </summary>
    public static ThemePreference SideTabTheme { get; } = new(
        ThemePreference.Default.Id,
        ThemePreference.Default.Name,
        AppearanceMode.System,
        PlatformProfile.Automatic,
        AccentPreference.FollowHost,
        tabStripPlacement: TabStripPlacement.Left);

    public static IReadOnlyList<StoredDefinition<FileProviderProfile>> FileProviderProfiles { get; } =
    [
        Stored(new FileProviderProfile(
            new FileProviderProfileId("release-artifacts"),
            FileProviderProfile.CurrentSchemaVersion,
            "release-artifacts",
            new FileProviderConfiguration.S3("release-artifacts", "eu-central-1"))),
        Stored(new FileProviderProfile(
            new FileProviderProfileId("staging-uploads"),
            FileProviderProfile.CurrentSchemaVersion,
            "staging-uploads",
            new FileProviderConfiguration.Sftp(
                new ConnectionId("staging-web"),
                "/var/uploads"))),
    ];

    public static IReadOnlyList<StoredDefinition<DatabaseConnectionProfile>> DatabaseConnections { get; } =
    [
        Stored(new DatabaseConnectionProfile(
            new DatabaseConnectionProfileId("core-warehouse"),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "core-warehouse",
            "postgres",
            "Host=warehouse.internal;Port=5432;Database=events;Username=reader",
            passwordSecret: new SecretRef("qa-database-password"),
            tunnelConnectionId: new ConnectionId("bastion-eu"))),
        Stored(new DatabaseConnectionProfile(
            new DatabaseConnectionProfileId("local-metrics"),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "local-metrics",
            "sqlite",
            "/Users/terion/metrics.db")),
    ];


    /// <summary>A C# sample for the syntax-highlight capture.</summary>
    public const string SampleCSharp = """
using System.Text;

namespace GhostShell.Core;

/// <summary>How a terminal session begins.</summary>
public sealed record ConnectionStartup
{
    public ConnectionStartup(string? directory = null, string? command = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        Command = command;
    }

    public string? Directory { get; }

    public string? Command { get; }

    public override string ToString()
    {
        var builder = new StringBuilder("startup");
        if (Directory is { } directory)
        {
            builder.Append($" in {directory}");
        }

        return builder.ToString(); // 2 + 2 == 4
    }
}
""";

    public static DefinitionCatalogSnapshot Snapshot { get; } = new(
        Connections,
        Layouts,
        Screens,
        Workspaces,
        [Stored(SideTabTheme)],
        TerminalProfiles,
        Keymaps,
        FileProviderProfiles,
        [Stored(QuickTerminalSettings.Default)])
    {
        DatabaseConnections = DatabaseConnections,
    };
}
