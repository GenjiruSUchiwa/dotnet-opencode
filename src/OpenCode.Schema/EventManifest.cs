namespace OpenCode.Schema;

/// <summary>
/// 1:1 port of EventManifest from packages/schema/src/event-manifest.ts
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
