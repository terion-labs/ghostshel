namespace GhostShell.Application;

/// <summary>
/// Provides deterministic terminal input, observation, and bounded waits.
/// </summary>
/// <remarks>
/// Exact text input and screen reads intentionally share the engine operations
/// exposed by <see cref="ITerminalProcess"/> and <see cref="ITerminalState"/>.
/// The separate views let callers request only the capability they need.
/// </remarks>
public interface ITerminalAutomation
{
    ValueTask WriteAsync(string text, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken);

    /// <summary>
    /// Encodes one platform keyboard event using the terminal's negotiated
    /// keyboard protocol. Unlike <see cref="WriteAsync"/>, this preserves
    /// physical identity, repeat/release state, and consumed modifiers.
    /// </summary>
    ValueTask SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Physical keyboard events are not supported by this terminal engine.");

    /// <summary>
    /// Sends one bounded character chord. Normal completion is the engine's
    /// irreversible delivery receipt; cancellation or failure means the chord
    /// did not cross that input boundary.
    /// </summary>
    ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken);

    ValueTask EnterAsync(CancellationToken cancellationToken);

    ValueTask InterruptAsync(CancellationToken cancellationToken);

    ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken);

    ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken);

    ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        CancellationToken cancellationToken);

    ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        CancellationToken cancellationToken);
}
