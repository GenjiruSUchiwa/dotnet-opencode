namespace OpenCode.Schema;

public static class ModelsDevEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Refreshed = new("models-dev.refreshed", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Refreshed);
}

public static class CatalogEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("catalog.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class AgentEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("agent.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class ConfigEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("config.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class CredentialEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("credential.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static readonly EphemeralEventDefinition<CredentialSwitchedEventData> Switched = new("credential.switched", OpenCodeJsonContext.Default.CredentialSwitchedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated, Switched);
}

public static class IntegrationEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("integration.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class ProjectEventDefinitions
{
    // project.updated contains Info.fields directly, not an { info: ... } wrapper.
    public static readonly EphemeralEventDefinition<ProjectInfo> Updated = new("project.updated", OpenCodeJsonContext.Default.ProjectInfo);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class WorktreeEventDefinitions
{
    public static readonly EphemeralEventDefinition<WorktreeUpdatedEventData> Updated = new("worktree.updated", OpenCodeJsonContext.Default.WorktreeUpdatedEventData);
    public static readonly DurableEventDefinition<WorktreeResolvedEventData> Resolved = new("worktree.resolved", 1, "projectID", OpenCodeJsonContext.Default.WorktreeResolvedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated, Resolved);
}

public static class SkillEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("skill.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class CommandEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("command.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class ReferenceEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("reference.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}

public static class InstallationEventDefinitions
{
    public static readonly EphemeralEventDefinition<InstallationVersionEventData> Updated = new("installation.updated", OpenCodeJsonContext.Default.InstallationVersionEventData);
    public static readonly EphemeralEventDefinition<InstallationVersionEventData> UpdateAvailable = new("installation.update-available", OpenCodeJsonContext.Default.InstallationVersionEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated, UpdateAvailable);
}

public static class VcsEventDefinitions
{
    public static readonly EphemeralEventDefinition<VcsBranchUpdatedEventData> BranchUpdated = new("vcs.branch.updated", OpenCodeJsonContext.Default.VcsBranchUpdatedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(BranchUpdated);
}

public static class McpEventDefinitions
{
    public static readonly EphemeralEventDefinition<McpStatusChangedEventData> ToolsChanged = new("mcp.tools.changed", OpenCodeJsonContext.Default.McpStatusChangedEventData);
    public static readonly EphemeralEventDefinition<McpStatusChangedEventData> ResourcesChanged = new("mcp.resources.changed", OpenCodeJsonContext.Default.McpStatusChangedEventData);
    public static readonly EphemeralEventDefinition<McpStatusChangedEventData> StatusChanged = new("mcp.status.changed", OpenCodeJsonContext.Default.McpStatusChangedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(ToolsChanged, ResourcesChanged, StatusChanged);
}

public static class ServerEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Connected = new("server.connected", OpenCodeJsonContext.Default.EmptyEventData);
    public static readonly EphemeralEventDefinition<EmptyEventData> Disposed = new("global.disposed", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Connected, Disposed);
}
