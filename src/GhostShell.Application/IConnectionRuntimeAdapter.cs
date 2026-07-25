using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Infrastructure boundary implemented once for every supported connection kind.
/// </summary>
public interface IConnectionRuntimeAdapter : IConnectionRuntime
{
    ConnectionKind Kind { get; }
}
