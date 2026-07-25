namespace GhostShell.Terminal;

public sealed record GhosttyAvailability(
    bool IsAvailable,
    string? LibraryName,
    string Detail);

