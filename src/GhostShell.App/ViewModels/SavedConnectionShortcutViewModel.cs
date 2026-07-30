using FluentIcons.Common;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// One actionable saved target in the workspace shortcut strip.
/// The launch list is projected from the target's domain capabilities.
/// </summary>
public sealed record SavedConnectionShortcutViewModel(
    PanelConnectionOptionViewModel.Target Target,
    string Name,
    string Kind,
    bool CanOpen,
    SavedConnectionLaunchViewModel DefaultLaunch,
    IReadOnlyList<SavedConnectionLaunchViewModel> AlternativeLaunches)
{
    public bool IsSingleAction => AlternativeLaunches.Count == 0;

    public bool HasAlternatives => AlternativeLaunches.Count > 0;
}

public sealed record SavedConnectionLaunchViewModel(
    PanelConnectionOptionViewModel.Target Target,
    PanelKind Panel,
    string Label,
    Symbol Icon);
