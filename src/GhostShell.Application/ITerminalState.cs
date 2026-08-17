namespace GhostShell.Application;

/// <summary>
/// Exposes canonical terminal state independently of its renderer and process.
/// </summary>
public interface ITerminalState
{
    ValueTask<TerminalScrollbackSnapshot> ReadScrollbackAsync(
        TerminalScrollbackReadInput input,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalScrollbackSnapshot>(new NotSupportedException(
            "Non-mutating terminal history projection is not supported by this terminal engine."));

    ValueTask<TerminalScrollbackFindResult> FindScrollbackAsync(
        TerminalScrollbackFindInput input,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalScrollbackFindResult>(new NotSupportedException(
            "Non-mutating terminal history search is not supported by this terminal engine."));

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
