namespace GhostShell.App.ViewModels;

public interface IProductComponentCatalog
{
    IReadOnlyList<ProductComponentViewModel> Components { get; }
}
