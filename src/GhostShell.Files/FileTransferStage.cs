namespace GhostShell.Files;

public enum FileTransferStage
{
    Reading,
    Writing,
    Committing,
    DeletingSource,
    Completed,
}
