using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App;

namespace GhostShell.App.Views;

public sealed partial class HistoryClearDialog : Window
{
    private readonly Func<RecentSessionClearCutoff> _captureCutoff;

    public HistoryClearDialog()
        : this(() => new RecentSessionClearCutoff(TimeProvider.System.GetUtcNow()))
    {
    }

    public HistoryClearDialog(Func<RecentSessionClearCutoff> captureCutoff)
    {
        _captureCutoff = captureCutoff ?? throw new ArgumentNullException(nameof(captureCutoff));
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close((RecentSessionClearCutoff?)null);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(_captureCutoff());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
