namespace OpenCode.Schema;

/// <summary>
/// Existing event-name conveniences. These constants are not a definition or membership inventory.
/// </summary>
public static class EventTypes
{
    // Session events
    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string SessionDeleted = "session.deleted";
    public const string SessionIdle = "session.idle";
    public const string SessionInboxEnqueued = "session.inbox.enqueued";
    public const string SessionInboxDelivered = "session.inbox.delivered";
    public const string SessionMessageUpdated = "session.message.updated";

    // Server events
    public const string ServerConnected = "server.connected";
    public const string ServerDisposed = "global.disposed";

    // FileSystem events
    public const string FileSystemChanged = "filesystem.changed";

    // Pty events
    public const string PtyCreated = "pty.created";
    public const string PtyUpdated = "pty.updated";
    public const string PtyExited = "pty.exited";
    public const string PtyDeleted = "pty.deleted";

    // Shell events
    public const string ShellCreated = "shell.created";
    public const string ShellExited = "shell.exited";
    public const string ShellDeleted = "shell.deleted";

    // MCP events
    public const string McpToolsChanged = "mcp.tools.changed";
    public const string McpResourcesChanged = "mcp.resources.changed";
    public const string McpStatusChanged = "mcp.status.changed";

    // Permission events
    public const string PermissionAsked = "permission.asked";
    public const string PermissionReplied = "permission.replied";

    // Worktree events
    public const string WorktreeUpdated = "worktree.updated";
    public const string WorktreeResolved = "worktree.resolved";

    // VCS events
    public const string VcsUpdated = "vcs.updated";
}

/// <summary>Only implemented definitions. A missing key means unreviewed, never private or invalid.</summary>
public static class EventManifest
{
    public const bool IsPublicInventoryComplete = false;
    public const bool IsSharedInventoryComplete = false;
    public const bool IsDurableInventoryComplete = false;

    private static IReadOnlyList<EventDefinition> Foundation { get; } = EventDefinitions.Inventory(
        [.. ModelsDevEventDefinitions.Definitions, .. CredentialEventDefinitions.Definitions,
         .. IntegrationEventDefinitions.Definitions, .. CatalogEventDefinitions.Definitions,
         .. AgentEventDefinitions.Definitions, .. SessionEventDefinitions.ImplementedDefinitions]);

    private static IReadOnlyList<EventDefinition> Features { get; } = EventDefinitions.Inventory(
        [.. FileSystemEventDefinitions.Definitions, .. ReferenceEventDefinitions.Definitions,
         .. PermissionEventDefinitions.Definitions, .. PluginEventDefinitions.Definitions, .. ProjectEventDefinitions.Definitions,
         .. WorktreeEventDefinitions.Definitions, .. CommandEventDefinitions.Definitions,
         .. ConfigEventDefinitions.Definitions, .. SkillEventDefinitions.Definitions,
         .. PtyEventDefinitions.Definitions, .. PersistentPtyEventDefinitions.Definitions,
         .. ShellEventDefinitions.Definitions, .. FormEventDefinitions.Definitions, .. WebSearchEventDefinitions.Definitions]);

    public static IReadOnlyList<EventDefinition> ImplementedPublicDefinitions { get; } =
        EventDefinitions.Inventory([.. Foundation, .. Features, .. SessionStatusEventDefinitions.Definitions,
            .. TuiEventDefinitions.Definitions, .. InstallationEventDefinitions.Definitions,
            .. VcsEventDefinitions.Definitions, McpEventDefinitions.StatusChanged, McpEventDefinitions.ResourcesChanged]);
    public static IReadOnlyDictionary<string, EventDefinition> ImplementedPublicLatest { get; } =
        EventDefinitions.Latest(ImplementedPublicDefinitions);

    // Preserve the source's broader Definitions membership and relative inventory order.
    public static IReadOnlyList<EventDefinition> ImplementedDefinitions { get; } =
        EventDefinitions.Inventory([.. Foundation, .. InstallationEventDefinitions.Definitions, .. Features,
            .. TuiEventDefinitions.Definitions, .. McpEventDefinitions.Definitions,
            .. SessionStatusEventDefinitions.Definitions, .. SessionCompactionEventDefinitions.Definitions,
            .. VcsEventDefinitions.Definitions, .. ServerEventDefinitions.Definitions]);
    public static IReadOnlyDictionary<string, EventDefinition> ImplementedLatest { get; } =
        EventDefinitions.Latest(ImplementedDefinitions);

    public static IReadOnlyDictionary<string, EventDefinition> ImplementedDurable { get; } =
        EventDefinitions.DurableMap([.. SessionEventDefinitions.ImplementedDurableDefinitions, WorktreeEventDefinitions.Resolved]);

    // No IsServer predicate: this subset must not suppress valid, not-yet-ported public events.
}
