using FluentIcons.Common;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// One actionable saved target offered by the chooser.
/// The launch list is projected from the target's domain capabilities.
///
/// A saved target is one thing whichever way it is opened, so it is one row with
/// the usual way on it and the others behind a chevron — rather than one row per
/// (target, adapter) pair, which listed the same host four times.
/// </summary>
public sealed record SavedConnectionShortcutViewModel(
    PanelConnectionOptionViewModel.Target Target,
    string Name,
    string Kind,
    string Detail,
    bool CanOpen,
    SavedConnectionLaunchViewModel DefaultLaunch,
    IReadOnlyList<SavedConnectionLaunchViewModel> AlternativeLaunches)
{
    public bool HasAlternatives => AlternativeLaunches.Count > 0;
}

public sealed record SavedConnectionLaunchViewModel(
    PanelConnectionOptionViewModel.Target Target,
    PanelKind Panel,
    string Label,
    Symbol Icon);
