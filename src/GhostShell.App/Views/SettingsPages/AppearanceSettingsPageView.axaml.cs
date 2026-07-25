using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.Core;

namespace GhostShell.App.Views.SettingsPages;

internal sealed record AppearanceSelection(
    AppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    AccentPreference Accent,
    double? TextScale);

internal sealed record AppearanceTextScaleOption(string DisplayName, double? Scale);

public sealed partial class AppearanceSettingsPageView : UserControl
{
    private IReadOnlyList<AppearanceTextScaleOption> _appearanceTextScaleOptions = [];

    public AppearanceSettingsPageView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    internal void ConfigureAppearanceControls(
        IReadOnlyList<PlatformProfile> platformProfiles,
        IReadOnlyList<AppearanceTextScaleOption> textScaleOptions)
    {
        ArgumentNullException.ThrowIfNull(platformProfiles);
        ArgumentNullException.ThrowIfNull(textScaleOptions);

        _appearanceTextScaleOptions = textScaleOptions;
        PlatformProfilePicker.ItemsSource = platformProfiles;
        ApplicationTextScalePicker.ItemsSource = textScaleOptions;
    }

    internal void ApplyAppearance(
        ThemePreference theme,
        AppearanceTextScaleOption selectedTextScale)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(selectedTextScale);

        SelectComboBoxItem(
            AppearanceModePicker,
            nameof(AppearanceModePicker),
            theme.Appearance.ToString());
        PlatformProfilePicker.SelectedItem = theme.PlatformProfile;
        SelectComboBoxItem(
            AccentModePicker,
            nameof(AccentModePicker),
            theme.Accent.Kind switch
            {
                AccentPreferenceKind.Custom => "Custom",
                AccentPreferenceKind.GhostShellBronze => "GhostSHELL bronze",
                _ => "Follow host",
            });

        ApplicationTextScalePicker.ItemsSource =
            _appearanceTextScaleOptions.Contains(selectedTextScale)
                ? _appearanceTextScaleOptions
                : [.. _appearanceTextScaleOptions, selectedTextScale];
        ApplicationTextScalePicker.SelectedItem = selectedTextScale;
        CustomAccentText.Text = theme.Accent.CustomColor?.ToString()
            ?? ThemePreference.BronzeFallback.ToString();
        UpdateCustomAccentAvailability();
    }

    internal AppearanceSelection CaptureAppearance()
    {
        var appearance = Enum.Parse<AppearanceMode>(
            SelectedText(AppearanceModePicker, nameof(AppearanceModePicker)));
        var profile = PlatformProfilePicker.SelectedItem is PlatformProfile selectedProfile
            ? selectedProfile
            : throw new InvalidOperationException(
                "The platform-profile selection is unavailable.");
        var accent = SelectedText(AccentModePicker, nameof(AccentModePicker)) switch
        {
            "Custom" => AccentPreference.Custom(
                RgbColor.Parse(CustomAccentText.Text ?? "#B8793A")),
            "GhostSHELL bronze" => AccentPreference.GhostShellBronze,
            _ => AccentPreference.FollowHost,
        };
        var textScale =
            ApplicationTextScalePicker.SelectedItem is AppearanceTextScaleOption selectedTextScale
                ? selectedTextScale.Scale
                : throw new InvalidOperationException(
                    "The application text-scale selection is unavailable.");

        return new(appearance, profile, accent, textScale);
    }

    private void OnAccentModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateCustomAccentAvailability();
    }

    private void OnSaveAppearanceClick(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(sender, e);

    private void UpdateCustomAccentAvailability() =>
        CustomAccentText.IsEnabled =
            SelectedTextOrDefault(AccentModePicker) == "Custom";

    private static string SelectedText(ComboBox comboBox, string controlName) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? throw new InvalidOperationException(
            $"The {controlName} selection is unavailable.");

    private static string? SelectedTextOrDefault(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static void SelectComboBoxItem(
        ComboBox comboBox,
        string controlName,
        string content)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Content?.ToString(),
                content,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The {controlName} control has no '{content}' option.");
    }
}
