namespace GhostShell.App.Views;

public sealed record DatabasePasswordPromptResult(
    string Password,
    bool SaveToCredentialStore);
