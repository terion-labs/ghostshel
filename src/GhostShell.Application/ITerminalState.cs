namespace GhostShell.Application;

/// <summary>
/// Exposes canonical terminal state independently of its renderer and process.
/// </summary>
public interface ITerminalState
{
    ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken);

    ValueTask ClearScrollbackAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Clearing terminal scrollback is not supported by this terminal engine."));

    ValueTask<TerminalFindResult> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalFindResult>(new NotSupportedException(
            "Finding terminal output is not supported by this terminal engine."));

    ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken);

    ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken);

    ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken);
}
