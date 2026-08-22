namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    private static App GhostShellApplication => Avalonia.Application.Current as App
        ?? throw new InvalidOperationException("The GhostSHELL application is unavailable.");

    private void OnNewWindowMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        GhostShellApplication.OpenNewWindow();
    }

    private async void OnNewTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await GhostShellApplication.OpenNewTabAsync(this);
    }

    private async void OnNewTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewTerminalAsync();
    }

    private async void OnCloseTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await GhostShellApplication.CloseTabAsync(this);
    }

    private void OnCommandPaletteMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ShowCommandPalette();
    }

    private void OnLauncherMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _ = NavigateToLauncherAsync();
    }

    private void OnQuickTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        GhostShellApplication.ToggleQuickTerminal();
    }

    private void OnToggleAgentMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ToggleAgentPanel();
    }

    private async void OnAddPanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowNewPanelChooserAsync();
    }

    private async void OnLayoutDesignerMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowLayoutDesignerAsync();
    }

    private async void OnPreviousTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await SelectRelativeTabAsync(-1);
    }

    private async void OnNextTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await SelectRelativeTabAsync(1);
    }

    private async void OnClosePanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestClosePanelAsync();
    }
}
