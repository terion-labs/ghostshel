using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

internal enum LauncherOverviewSection
{
    Home,
    Connections,
    Screens,
}

public sealed partial class LauncherView : UserControl
{
    public LauncherView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddConnectionRequested;

    public event EventHandler<RoutedEventArgs>? CancelHistoryExportRequested;

    public event EventHandler<RoutedEventArgs>? ClearRecentSessionsRequested;

    public event EventHandler<RoutedEventArgs>? DeleteConnectionRequested;

    public event EventHandler<RoutedEventArgs>? EditConnectionRequested;

    public event EventHandler<RoutedEventArgs>? EditScreenRequested;

    public event EventHandler<RoutedEventArgs>? ExportAllHistoryRequested;

    public event EventHandler<RoutedEventArgs>? ExportFilteredHistoryRequested;

    public event EventHandler<RoutedEventArgs>? FinishOnboardingRequested;

    public event EventHandler<KeyEventArgs>? HistorySearchKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? ImportDefinitionsRequested;

    public event EventHandler<RoutedEventArgs>? LauncherConnectionsRequested;

    public event EventHandler<RoutedEventArgs>? LauncherHistoryRequested;

    public event EventHandler<RoutedEventArgs>? LauncherHomeRequested;

    public event EventHandler<RoutedEventArgs>? LauncherScreensRequested;

    public event EventHandler<RoutedEventArgs>? OpenConnectionRequested;

    public event EventHandler<RoutedEventArgs>? OpenRecentSessionRequested;

    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;

    public event EventHandler<RoutedEventArgs>? OpenSelectedHistorySessionRequested;

    public event EventHandler<RoutedEventArgs>? ResetRecentSessionHistoryRequested;

    public event EventHandler<RoutedEventArgs>? RetryOnboardingRequested;

    public event EventHandler<RoutedEventArgs>? RetryRecentSessionHistoryRequested;

    public event EventHandler<RoutedEventArgs>? ReviewHistoryPrivacyRequested;

    public event EventHandler<RoutedEventArgs>? SaveHistoryRetentionRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewItemRequested;

    public event EventHandler<RoutedEventArgs>? ShowSettingsRequested;

    internal void FocusHomeNavigation() =>
        LauncherHomeButton.FocusItem();

    internal void FocusHistoryNavigation() =>
        LauncherHistoryButton.FocusItem();

    internal void FocusHistorySearch() =>
        HistorySearchBox.Focus(NavigationMethod.Tab);

    internal void FocusOnboardingFinish() =>
        OnboardingFinishButton.Focus(NavigationMethod.Tab);

    internal void FocusOverviewSection(
        LauncherOverviewSection section,
        bool resetScroll = false)
    {
        if (resetScroll)
        {
            LauncherScrollViewer.Offset = default;
        }

        Control target = section switch
        {
            LauncherOverviewSection.Home => LauncherHomeSection,
            LauncherOverviewSection.Connections => LauncherConnectionsSection,
            LauncherOverviewSection.Screens => LauncherScreensSection,
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        target.BringIntoView();
        target.Focus();
    }

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) =>
        AddConnectionRequested?.Invoke(sender, e);

    private void OnCancelHistoryExportClick(object? sender, RoutedEventArgs e) =>
        CancelHistoryExportRequested?.Invoke(sender, e);

    private void OnClearRecentSessionsClick(object? sender, RoutedEventArgs e) =>
        ClearRecentSessionsRequested?.Invoke(sender, e);

    private void OnDeleteConnectionClick(object? sender, RoutedEventArgs e) =>
        DeleteConnectionRequested?.Invoke(sender, e);

    private void OnEditConnectionClick(object? sender, RoutedEventArgs e) =>
        EditConnectionRequested?.Invoke(sender, e);

    private void OnEditScreenClick(object? sender, RoutedEventArgs e) =>
        EditScreenRequested?.Invoke(sender, e);

    private void OnExportAllHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportAllHistoryRequested?.Invoke(sender, e);

    private void OnExportFilteredHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportFilteredHistoryRequested?.Invoke(sender, e);

    private void OnFinishOnboardingClick(object? sender, RoutedEventArgs e) =>
        FinishOnboardingRequested?.Invoke(sender, e);

    private void OnHistorySearchKeyDown(object? sender, KeyEventArgs e) =>
        HistorySearchKeyDownRequested?.Invoke(sender, e);

    private void OnImportDefinitionsClick(object? sender, RoutedEventArgs e) =>
        ImportDefinitionsRequested?.Invoke(sender, e);

    private void OnLauncherConnectionsClick(object? sender, RoutedEventArgs e) =>
        LauncherConnectionsRequested?.Invoke(sender, e);

    private void OnLauncherHistoryClick(object? sender, RoutedEventArgs e) =>
        LauncherHistoryRequested?.Invoke(sender, e);

    private void OnLauncherHomeClick(object? sender, RoutedEventArgs e) =>
        LauncherHomeRequested?.Invoke(sender, e);

    private void OnLauncherScreensClick(object? sender, RoutedEventArgs e) =>
        LauncherScreensRequested?.Invoke(sender, e);

    private void OnOpenConnectionClick(object? sender, RoutedEventArgs e) =>
        OpenConnectionRequested?.Invoke(sender, e);

    private void OnOpenRecentSessionClick(object? sender, RoutedEventArgs e) =>
        OpenRecentSessionRequested?.Invoke(sender, e);

    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnOpenSelectedHistorySessionClick(object? sender, RoutedEventArgs e) =>
        OpenSelectedHistorySessionRequested?.Invoke(sender, e);

    private void OnResetRecentSessionHistoryClick(object? sender, RoutedEventArgs e) =>
        ResetRecentSessionHistoryRequested?.Invoke(sender, e);

    private void OnRetryOnboardingClick(object? sender, RoutedEventArgs e) =>
        RetryOnboardingRequested?.Invoke(sender, e);

    private void OnRetryRecentSessionHistoryClick(object? sender, RoutedEventArgs e) =>
        RetryRecentSessionHistoryRequested?.Invoke(sender, e);

    private void OnReviewHistoryPrivacyClick(object? sender, RoutedEventArgs e) =>
        ReviewHistoryPrivacyRequested?.Invoke(sender, e);

    private void OnSaveHistoryRetentionClick(object? sender, RoutedEventArgs e) =>
        SaveHistoryRetentionRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowNewItemClick(object? sender, RoutedEventArgs e) =>
        ShowNewItemRequested?.Invoke(sender, e);

    private void OnShowSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsRequested?.Invoke(sender, e);
}
