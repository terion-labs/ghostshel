using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.Application;

namespace GhostShell.App.Views;

public sealed partial class RecoveryDialog : Window
{
    private bool _choiceMade;

    public RecoveryDialog()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_choiceMade)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e) =>
        Finish(sender, e, RecoveryChoice.Restore);

    private void OnSafeModeClick(object? sender, RoutedEventArgs e) =>
        Finish(sender, e, RecoveryChoice.SafeMode);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) =>
        Finish(sender, e, RecoveryChoice.DiscardRuntimeState);

    private void Finish(object? sender, RoutedEventArgs e, RecoveryChoice choice)
    {
        _ = sender;
        _ = e;
        _choiceMade = true;
        Close(choice);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
