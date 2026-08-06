using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.Core;

namespace GhostShell.App.Views.SettingsPages;

internal sealed record AppearanceSelection(
    AppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    AccentPreference Accent,
    double? TextScale,
    double? CornerRadius,
    InterfaceDensity Density,
    bool ShowTabBar,
    bool ShowWorkspacesPanel,
    TabStripPlacement TabStripPlacement,
    WorkspacePanelPlacement WorkspacePanelPlacement,
    bool IsTranslucent,
    int BackdropOpacityPercent,
    bool HasGlassPanels,
    bool OverridesBackdropOpacity);

internal sealed record AppearanceTextScaleOption(string DisplayName, double? Scale);

public sealed partial class AppearanceSettingsPageView : UserControl
{
    private IReadOnlyList<AppearanceTextScaleOption> _appearanceTextScaleOptions = [];

    public AppearanceSettingsPageView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised whenever a control on the page changes. Appearance has no save
    /// step: the shell persists and applies each change as it is made.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? AppearanceChanged;

    public event EventHandler<RoutedEventArgs>? SelectTerminalPaletteRequested;

    /// <summary>
    /// Raised with the palette field to fill. The shell owns sampling because it
    /// owns the window the colour is read from.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? PickColorRequested;

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

        _isLoading = true;
        try
        {
            AppearanceModeSystem.IsChecked = theme.Appearance == AppearanceMode.System;
            AppearanceModeDark.IsChecked = theme.Appearance == AppearanceMode.Dark;
            AppearanceModeLight.IsChecked = theme.Appearance == AppearanceMode.Light;
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

            // A null override means "follow the platform profile"; the slider has no
            // null, so it rests at the profile's own radius until the user moves it.
            CornerRadiusSlider.Value = theme.CornerRadiusOverride ?? DefaultCornerRadius;
            TranslucencyToggle.IsChecked = theme.IsTranslucent;
            GlassPanelsToggle.IsChecked = theme.HasGlassPanels;
            OverrideOpacityToggle.IsChecked = theme.OverridesBackdropOpacity;
            BackdropOpacitySlider.Value = theme.BackdropOpacityPercent;
            DensityCompact.IsChecked = theme.Density == InterfaceDensity.Compact;
            DensityCozy.IsChecked = theme.Density == InterfaceDensity.Cozy;
            DensityComfortable.IsChecked = theme.Density == InterfaceDensity.Comfortable;
            ShowTabBarSwitch.IsChecked = theme.ShowTabBar;
            ShowWorkspacesPanelSwitch.IsChecked = theme.ShowWorkspacesPanel;
            TabPlacementTop.IsChecked = theme.TabStripPlacement == TabStripPlacement.Top;
            TabPlacementBottom.IsChecked = theme.TabStripPlacement == TabStripPlacement.Bottom;
            WorkspacePanelLeft.IsChecked =
                theme.WorkspacePanelPlacement == WorkspacePanelPlacement.Left;
            WorkspacePanelRight.IsChecked =
                theme.WorkspacePanelPlacement == WorkspacePanelPlacement.Right;
        }
        finally
        {
            _isLoading = false;
        }
    }

    internal AppearanceSelection CaptureAppearance()
    {
        var appearance = AppearanceModeDark.IsChecked == true
            ? AppearanceMode.Dark
            : AppearanceModeLight.IsChecked == true
                ? AppearanceMode.Light
                : AppearanceMode.System;
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

        return new(
            appearance,
            profile,
            accent,
            textScale,
            CornerRadiusSlider.Value,
            SelectedDensity(),
            ShowTabBarSwitch.IsChecked == true,
            ShowWorkspacesPanelSwitch.IsChecked == true,
            SelectedTabStripPlacement(),
            WorkspacePanelRight.IsChecked == true
                ? WorkspacePanelPlacement.Right
                : WorkspacePanelPlacement.Left,
            TranslucencyToggle.IsChecked == true,
            (int)Math.Round(BackdropOpacitySlider.Value),
            GlassPanelsToggle.IsChecked == true,
            OverrideOpacityToggle.IsChecked == true);
    }

    /// <summary>
    /// What the slider shows when the theme carries no radius of its own.
    ///
    /// Eight was every platform's answer, and on macOS 26 it is nobody's: the
    /// system rounds windows far harder, and concentrically — the radius
    /// follows whatever sits at the top of the window, so Apple publishes no
    /// single number to copy. This is the shell's own answer for that look,
    /// not a value read from the system.
    /// </summary>
    private static double DefaultCornerRadius =>
        OperatingSystem.IsMacOSVersionAtLeast(26) ? 26 : 8;

    private TabStripPlacement SelectedTabStripPlacement() =>
        TabPlacementBottom.IsChecked == true
            ? TabStripPlacement.Bottom
            : TabPlacementLeft.IsChecked == true
                ? TabStripPlacement.Left
                : TabPlacementRight.IsChecked == true
                    ? TabStripPlacement.Right
                    : TabStripPlacement.Top;

    private InterfaceDensity SelectedDensity() =>
        DensityCompact.IsChecked == true
            ? InterfaceDensity.Compact
            : DensityComfortable.IsChecked == true
                ? InterfaceDensity.Comfortable
                : InterfaceDensity.Cozy;

    /// <summary>
    /// The segmented control is a set of toggle buttons, so selecting one has to
    /// clear the others; a checked button clicked again stays checked rather than
    /// leaving the group with no answer.
    /// </summary>
    private void OnDensityClick(object? sender, RoutedEventArgs e)
    {
        foreach (var option in new[] { DensityCompact, DensityCozy, DensityComfortable })
        {
            option.IsChecked = ReferenceEquals(option, sender);
        }

        // The segments deliberately do not wire IsCheckedChanged — this handler
        // reassigns every segment's checked state and would echo one click as
        // three change events. It must therefore report the change itself, or
        // the density picker saves nothing and the setting is a dead control.
        OnAppearanceChanged(sender, e);
    }

    /// <summary>
    /// Loading stored values sets the same controls this page listens to, so the
    /// reload is fenced; without it every save would echo back as a fresh change.
    /// </summary>
    private bool _isLoading;

    private void OnAppearanceChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppearanceChanged?.Invoke(sender, e);
    }

    /// <summary>
    /// Changing the accent source both re-enables the custom colour and is itself
    /// a change to commit — switching from "Follow host" to the bronze accent has
    /// to apply live like every other control on this page.
    /// </summary>
    private void OnAccentModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCustomAccentAvailability();
        OnAppearanceChanged(sender, e);
    }

    /// <summary>
    /// The colour picker reports its own event type, so it needs a matching
    /// signature to reach the same live-commit path as every other control.
    /// </summary>
    private void OnColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _ = e;
        OnAppearanceChanged(sender, new RoutedEventArgs());
    }

    /// <summary>Enter commits a typed value without waiting for focus to move.</summary>
    private void OnCommitKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)
        {
            e.Handled = true;
            OnAppearanceChanged(sender, new RoutedEventArgs());
        }
    }

    private void OnSelectTerminalPaletteClick(object? sender, RoutedEventArgs e) =>
        SelectTerminalPaletteRequested?.Invoke(sender, e);

    private void OnPickColorClick(object? sender, RoutedEventArgs e) =>
        PickColorRequested?.Invoke(sender, e);

    private void UpdateCustomAccentAvailability()
    {
        var isCustom = SelectedTextOrDefault(AccentModePicker) == "Custom";
        CustomAccentText.IsEnabled = isCustom;
        CustomAccentPicker.IsEnabled = isCustom;
        CustomAccentEyedropper.IsEnabled = isCustom;
    }

    /// <summary>
    /// Writes a colour into the accent field from outside, used by the screen
    /// eyedropper. The picker and the hex box are kept in step.
    /// </summary>
    internal void SetCustomAccent(Avalonia.Media.Color color)
    {
        CustomAccentText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SyncCustomAccentPicker();
        AppearanceChanged?.Invoke(this, new RoutedEventArgs());
    }

    /// <summary>
    /// The hex box is the stored value; the picker mirrors it. Both edit the same
    /// accent, so each has to follow the other without looping.
    /// </summary>
    private bool _syncingCustomAccent;

    private void SyncCustomAccentPicker()
    {
        if (!Avalonia.Media.Color.TryParse(CustomAccentText.Text, out var color))
        {
            return;
        }

        _syncingCustomAccent = true;
        try
        {
            CustomAccentPicker.Color = color;
        }
        finally
        {
            _syncingCustomAccent = false;
        }
    }

    private void OnCustomAccentTextChanged(object? sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_syncingCustomAccent)
        {
            SyncCustomAccentPicker();
        }
    }

    private void OnCustomAccentPicked(object? sender, ColorChangedEventArgs e)
    {
        if (_syncingCustomAccent || _isLoading)
        {
            return;
        }

        _syncingCustomAccent = true;
        try
        {
            CustomAccentText.Text = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        }
        finally
        {
            _syncingCustomAccent = false;
        }

        OnAppearanceChanged(sender, new RoutedEventArgs());
    }

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
