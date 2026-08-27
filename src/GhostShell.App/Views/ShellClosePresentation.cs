using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.App.Views;

internal sealed record ShellClosePresentation(
    Func<Task<bool>> ConfirmLayoutDiscardAsync,
    Func<string, string, Task<bool>> ConfirmDiscardAsync,
    Func<CloseScopeResult.ConfirmationRequired, Task<bool>> ConfirmScopeAsync,
    Func<string, Task> ShowErrorAsync,
    Action RestoreFocus,
    Action FocusCurrentRoute,
    Action CloseWindow)
{
    public static ShellClosePresentation ForWindow(
        Window owner,
        ShellFocusNavigator focus) => new(
        () => Confirmations.DiscardChanges().ShowDialog<bool>(owner),
        (title, detail) => Confirmations.DiscardChanges(title, detail)
            .ShowDialog<bool>(owner),
        confirmation => Confirmations.CloseScope(confirmation)
            .ShowDialog<bool>(owner),
        message => Confirmations.OperationError(message).ShowDialog(owner),
        focus.RestoreAfterCancelledClose,
        focus.FocusCurrentRoute,
        owner.Close);
}
