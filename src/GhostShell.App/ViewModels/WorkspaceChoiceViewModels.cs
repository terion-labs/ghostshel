using FluentIcons.Common;

namespace GhostShell.App.ViewModels;

/// <summary>
/// One selectable tile in the workspace icon grid.
///
/// Selection lives on the item rather than on the grid because Avalonia can only
/// bind a style class from a single value, and comparing the item with the
/// editor's current choice needs both. The editor owns a private set of these,
/// so marking one selected never leaks into another editor's grid.
/// </summary>
public sealed class WorkspaceIconChoiceViewModel(WorkspaceIconOption option)
    : ObservableObject
{
    private bool _isSelected;

    public string Id => option.Id;

    public string Name => option.Name;

    public Symbol Symbol => option.Symbol;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool Matches(string term) => option.Matches(term);
}

/// <summary>One selectable swatch in the workspace accent row.</summary>
public sealed class WorkspaceAccentChoiceViewModel(WorkspaceAccentOption option)
    : ObservableObject
{
    private bool _isSelected;

    public string Name => option.Name;

    public string Hex => option.Hex;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
