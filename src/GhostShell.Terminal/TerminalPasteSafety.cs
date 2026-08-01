using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

internal static class TerminalPasteSafety
{
    public static bool RequiresConfirmation(
        TerminalPasteInput input,
        TerminalPasteSafetyPolicy policy,
        bool bracketedPasteEnabled)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.ContainsUnsafeContent
            && !input.ConfirmedUnsafe
            && policy switch
            {
                TerminalPasteSafetyPolicy.AllowUnsafe => false,
                TerminalPasteSafetyPolicy.ProtectUnsafe => !bracketedPasteEnabled,
                TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed => true,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    policy,
                    "Unknown paste policy."),
            };
    }
}
