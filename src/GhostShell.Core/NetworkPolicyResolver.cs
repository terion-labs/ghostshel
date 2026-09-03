namespace GhostShell.Core;

public static class NetworkPolicyResolver
{
    public static NetworkPolicy Resolve(
        ApplicationNetworkSettings applicationSettings,
        WorkspaceDefinition workspace)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.NetworkOverride ?? applicationSettings.Policy;
    }
}
