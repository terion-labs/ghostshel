namespace GhostShell.Infrastructure;

public interface IConnectionExecutableLocator
{
    string? Find(string executable);
}
