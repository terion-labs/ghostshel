namespace GhostShell.App.ViewModels;

public enum MainWindowRole
{
    Primary,
    Additional,
}

public delegate MainWindowViewModel MainWindowViewModelFactory();
