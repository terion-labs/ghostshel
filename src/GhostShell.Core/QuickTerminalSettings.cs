using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Durable behavior for the single process-wide Quick Terminal. Percent-like values are stored
/// as fractions so the persisted payload remains independent of desktop pixel density.
/// </summary>
public sealed record QuickTerminalSettings : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const double MinimumHeightFraction = 0.25;
    public const double MaximumHeightFraction = 0.90;
    public const double MinimumOpacity = 0.00;
    public const double MaximumOpacity = 1.00;
    public const int MaximumBlurRadius = 100;
    public const int MaximumAnimationDurationMilliseconds = 1_000;

    public static QuickTerminalSettingsId DefaultId { get; } =
        new("builtin.quick-terminal.default");

    public static QuickTerminalSettings Default { get; } = new(
        DefaultId,
        "Quick Terminal",
        new KeyStroke("GRAVE", KeyModifiers.Meta),
        QuickTerminalMonitorPolicy.MainWindow,
        heightFraction: 0.55,
        opacity: 0.82,
        blurRadius: 24,
        animateSlide: true,
        animationDurationMilliseconds: 180,
        reduceMotion: false,
        restoreLastSession: true,
        hideOnFocusLoss: true);

    [JsonConstructor]
    public QuickTerminalSettings(
        QuickTerminalSettingsId id,
        string name,
        KeyStroke hotkey,
        QuickTerminalMonitorPolicy monitorPolicy,
        double heightFraction,
        double opacity,
        int blurRadius,
        bool animateSlide,
        int animationDurationMilliseconds,
        bool reduceMotion,
        bool restoreLastSession,
        bool hideOnFocusLoss)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.IsNullOrWhiteSpace(hotkey.Key)
            || hotkey.Key.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException(
                "A Quick Terminal hotkey must fit on one configuration line.",
                nameof(hotkey));
        }

        if (!Enum.IsDefined(monitorPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(monitorPolicy),
                monitorPolicy,
                "Unknown Quick Terminal monitor policy.");
        }

        if (!double.IsFinite(heightFraction)
            || heightFraction is < MinimumHeightFraction or > MaximumHeightFraction)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heightFraction),
                heightFraction,
                $"Quick Terminal height must be between {MinimumHeightFraction:P0} and {MaximumHeightFraction:P0}.");
        }

        if (!double.IsFinite(opacity) || opacity is < MinimumOpacity or > MaximumOpacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opacity),
                opacity,
                $"Quick Terminal opacity must be between {MinimumOpacity:P0} and {MaximumOpacity:P0}.");
        }

        if (blurRadius is < 0 or > MaximumBlurRadius)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blurRadius),
                blurRadius,
                $"Quick Terminal blur must be between 0 and {MaximumBlurRadius} pixels.");
        }

        if (animationDurationMilliseconds is < 0 or > MaximumAnimationDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationDurationMilliseconds),
                animationDurationMilliseconds,
                $"Quick Terminal animation must be between 0 and {MaximumAnimationDurationMilliseconds} milliseconds.");
        }

        Id = id;
        Name = name;
        Hotkey = hotkey;
        MonitorPolicy = monitorPolicy;
        HeightFraction = heightFraction;
        Opacity = opacity;
        BlurRadius = blurRadius;
        AnimateSlide = animateSlide;
        AnimationDurationMilliseconds = animationDurationMilliseconds;
        ReduceMotion = reduceMotion;
        RestoreLastSession = restoreLastSession;
        HideOnFocusLoss = hideOnFocusLoss;
    }

    public static DefinitionKind Kind => DefinitionKind.QuickTerminalSettings;

    public QuickTerminalSettingsId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public KeyStroke Hotkey { get; }

    public QuickTerminalMonitorPolicy MonitorPolicy { get; }

    public double HeightFraction { get; }

    public double Opacity { get; }

    public int BlurRadius { get; }

    public bool AnimateSlide { get; }

    public int AnimationDurationMilliseconds { get; }

    public bool ReduceMotion { get; }

    public bool RestoreLastSession { get; }

    public bool HideOnFocusLoss { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);
}
