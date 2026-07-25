namespace GhostShell.Core;

public static class WorkspaceValidator
{
    public static DefinitionValidationResult Validate(WorkspaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DefinitionValidationIssue> issues = [];

        ValidateHeader(definition, issues);
        foreach (var duplicate in definition.Entries
                     .GroupBy(entry => entry.Id.Value, StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                "Workspace entry IDs must be present and unique.",
                duplicate.Key));
        }

        foreach (var entry in definition.Entries)
        {
            ValidateEntry(entry, issues);
        }

        return new(issues);
    }

    private static void ValidateHeader(
        WorkspaceDefinition definition,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
        {
            issues.Add(new(DefinitionValidationCode.Required, "A workspace ID is required."));
        }

        if (definition.SchemaVersion < 1)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidSchemaVersion,
                "A workspace schema version must be at least one.",
                definition.Id.Value));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A workspace name is required.",
                definition.Id.Value));
        }

        if (!WorkspaceDefinition.IsValidIcon(definition.Icon))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A workspace icon must be a lowercase semantic identifier.",
                definition.Id.Value));
        }

        if (definition.AgentPolicyOverride is { } policy
            && !policy.IsValidForDurableStorage())
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidAgentPolicy,
                "A saved workspace agent policy must be structurally valid. "
                    + "YOLO is run-local and cannot be persisted.",
                definition.Id.Value));
        }
    }

    private static void ValidateEntry(
        WorkspaceEntry entry,
        ICollection<DefinitionValidationIssue> issues)
    {
        switch (entry)
        {
            case WorkspaceEntry.ConnectionReference connection
                when string.IsNullOrWhiteSpace(connection.ConnectionId.Value):
                issues.Add(new(
                    DefinitionValidationCode.MissingDependency,
                    "A workspace connection entry requires a connection ID.",
                    entry.Id.Value));
                break;

            case WorkspaceEntry.ScreenReference screen
                when string.IsNullOrWhiteSpace(screen.ScreenId.Value):
                issues.Add(new(
                    DefinitionValidationCode.MissingDependency,
                    "A workspace screen entry requires a screen ID.",
                    entry.Id.Value));
                break;

            case WorkspaceEntry.Tab tab:
                ValidateTab(tab, issues);
                break;
        }
    }

    private static void ValidateTab(
        WorkspaceEntry.Tab tab,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(tab.Name)
            || string.IsNullOrWhiteSpace(tab.LayoutId.Value)
            || tab.Panels.Count == 0)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A workspace-only tab requires a name, layout, and at least one panel.",
                tab.Id.Value));
        }

        foreach (var panel in tab.Panels)
        {
            ScreenValidator.ValidatePanel(panel, issues);
        }

        var hasDuplicatePanels = tab.Panels
            .GroupBy(panel => panel.Id.Value, StringComparer.Ordinal)
            .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        var hasDuplicateSlots = tab.Panels
            .GroupBy(panel => panel.SlotId.Value, StringComparer.Ordinal)
            .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (hasDuplicatePanels || hasDuplicateSlots)
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                "Workspace-only tab panel and slot IDs must be unique.",
                tab.Id.Value));
        }
    }
}
