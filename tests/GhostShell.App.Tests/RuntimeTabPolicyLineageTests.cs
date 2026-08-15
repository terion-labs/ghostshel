using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

/// <summary>
/// A tab belongs to the workspace it is in.
///
/// Recovery refuses a workspace whose tabs contradict its policy lineage, and
/// most of the places that build a tab passed no policy at all — so a browser,
/// file, monitor or database tab added to a workspace that had one broke every
/// snapshot from that moment on. The shell reported it as "runtime recovery
/// state could not be prepared" and kept saying it.
/// </summary>
public sealed class RuntimeTabPolicyLineageTests
{
    [Fact]
    public void A_tab_that_brought_no_policy_takes_the_workspace_s()
    {
        var workspace = WorkspaceGovernedBy(WorkspaceLineage());
        var tab = new RuntimeTabViewModel(new TabInstanceId("adopting"), "Files", "Local");
        Assert.Empty(tab.AgentPolicy.Sources);

        workspace.Tabs.Add(tab);

        Assert.Equal(workspace.AgentPolicy, tab.AgentPolicy);
    }

    /// <summary>
    /// A tab with provenance of its own — a saved screen, a connection resolved
    /// against its definition — keeps it. Adoption fills a gap; it does not
    /// overwrite an answer.
    /// </summary>
    [Fact]
    public void A_tab_that_brought_its_own_policy_keeps_it()
    {
        var workspace = WorkspaceGovernedBy(WorkspaceLineage());
        var own = new RuntimeAgentPolicyProvenance(
            AgentPolicy.Default,
            [
                new RuntimeAgentPolicyProvenance.Source(
                    new DefinitionKey(ScreenDefinition.Kind, "screen-one"),
                    1),
            ]);
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("own"),
            "Screen",
            "Screen",
            agentPolicy: own);

        workspace.Tabs.Add(tab);

        Assert.Equal(own, tab.AgentPolicy);
    }

    /// <summary>
    /// The whole point: the snapshot the shell writes on every change is
    /// preparable for a workspace built the way the shell builds one.
    /// </summary>
    [Fact]
    public void A_workspace_with_an_adopted_tab_can_be_written_to_recovery()
    {
        var workspace = WorkspaceGovernedBy(WorkspaceLineage());
        var tab = new RuntimeTabViewModel(new TabInstanceId("adopting"), "Files", "Local");
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            new PanelInstanceId("panel"),
            PanelKind.FileViewer,
            "Files",
            "LOCAL",
            "unavailable"));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        _ = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
    }

    [Fact]
    public void Workspace_override_inherits_global_conversation_maintenance_models()
    {
        var compaction = new AgentModelSelection("global", "compact-model");
        var title = new AgentModelSelection("global", "title-model");
        var global = AgentPolicy.Default with
        {
            Provider = "global",
            Model = "default-model",
            CompactionModel = compaction,
            TitleModel = title,
        };
        var workspace = AgentPolicy.Default with
        {
            Provider = "workspace",
            Model = "workspace-model",
            CompactionModel = null,
            TitleModel = null,
        };
        var provenance = new RuntimeAgentPolicyProvenance(global).WithOverride(
            workspace,
            new DefinitionKey(WorkspaceDefinition.Kind, "workspace"),
            revision: 1);

        Assert.Equal("workspace", provenance.EffectivePolicy.Provider);
        Assert.Equal("workspace-model", provenance.EffectivePolicy.Model);
        Assert.Equal(compaction, provenance.EffectivePolicy.CompactionModel);
        Assert.Equal(title, provenance.EffectivePolicy.TitleModel);
    }

    private static RuntimeAgentPolicyProvenance WorkspaceLineage() =>
        new(
            AgentPolicy.Default,
            [
                new RuntimeAgentPolicyProvenance.Source(
                    new DefinitionKey(WorkspaceDefinition.Kind, "default"),
                    1),
            ]);

    private static RuntimeWorkspaceViewModel WorkspaceGovernedBy(
        RuntimeAgentPolicyProvenance policy) =>
        new(
            WorkspaceInstanceId.New(),
            "Main",
            "Bronze",
            [],
            policy);
}
