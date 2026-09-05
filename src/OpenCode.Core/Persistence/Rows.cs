namespace OpenCode.Core.Persistence;

// Storage rows deliberately use scalar IDs and opaque JSON strings. Domain
// factories/serializers remain at the boundary; EF never owns their wire models.
internal sealed class AccountStateRow
{
    public long id { get; set; }
    public string? active_account_id { get; set; }
    public string? active_org_id { get; set; }
}
internal sealed class AccountRow
{
    public string id { get; set; } = "";
    public string email { get; set; } = "";
    public string url { get; set; } = "";
    public string access_token { get; set; } = "";
    public string refresh_token { get; set; } = "";
    public long? token_expiry { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class ControlAccountRow
{
    public string email { get; set; } = "";
    public string url { get; set; } = "";
    public string access_token { get; set; } = "";
    public string refresh_token { get; set; } = "";
    public long? token_expiry { get; set; }
    public long active { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class CredentialRow
{
    public string id { get; set; } = "";
    public string? integration_id { get; set; }
    public string label { get; set; } = "";
    public string value { get; set; } = "";
    public string? connector_id { get; set; }
    public string? method_id { get; set; }
    public long? active { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class EventSequenceRow
{
    public string aggregate_id { get; set; } = "";
    public long seq { get; set; }
    public string? owner_id { get; set; }
}
internal sealed class EventRow
{
    public string id { get; set; } = "";
    public string aggregate_id { get; set; } = "";
    public long seq { get; set; }
    public double created { get; set; }
    public string type { get; set; } = "";
    public string data { get; set; } = "";
}
internal sealed class KvRow
{
    public string key { get; set; } = "";
    public string value { get; set; } = "";
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class PermissionRow
{
    public string id { get; set; } = "";
    public string project_id { get; set; } = "";
    public string action { get; set; } = "";
    public string resource { get; set; } = "";
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class ProjectDirectoryRow
{
    public string project_id { get; set; } = "";
    public string directory { get; set; } = "";
    public string? type { get; set; }
    public string? strategy { get; set; }
    public long time_created { get; set; }
}
internal sealed class ProjectRow
{
    public string id { get; set; } = "";
    public string worktree { get; set; } = "";
    public string? vcs { get; set; }
    public string? name { get; set; }
    public string? icon_url { get; set; }
    public string? icon_url_override { get; set; }
    public string? icon_color { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
    public long? time_initialized { get; set; }
    public string sandboxes { get; set; } = "[]";
    public string? commands { get; set; }
}
internal sealed class InstructionBlobRow
{
    public string hash { get; set; } = "";
    public string? value { get; set; }
}
internal sealed class InstructionEntryRow
{
    public string session_id { get; set; } = "";
    public string key { get; set; } = "";
    public string? value { get; set; }
    public bool removed { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
}
internal sealed class InstructionStateRow
{
    public string session_id { get; set; } = "";
    public long epoch_start { get; set; }
    public long through_seq { get; set; }
    public string initial_values { get; set; } = "";
    public string current_values { get; set; } = "";
}
internal sealed class InboxRow
{
    public string id { get; set; } = "";
    public string session_id { get; set; } = "";
    public string type { get; set; } = "";
    public string payload { get; set; } = "";
    public string delivery { get; set; } = "";
    public long enqueued_seq { get; set; }
    public long time_created { get; set; }
}
internal sealed class MessageRow
{
    public string id { get; set; } = "";
    public string session_id { get; set; } = "";
    public string type { get; set; } = "";
    public long seq { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
    public string data { get; set; } = "";
}
internal sealed class PendingRow
{
    public string id { get; set; } = "";
    public string session_id { get; set; } = "";
    public string type { get; set; } = "";
    public string data { get; set; } = "";
    public string? delivery { get; set; }
    public long admitted_seq { get; set; }
    public long time_created { get; set; }
}
internal sealed class SessionRow
{
    public string id { get; set; } = "";
    public string project_id { get; set; } = "";
    public string? workspace_id { get; set; }
    public string? parent_id { get; set; }
    public string? fork_session_id { get; set; }
    public string? fork_boundary { get; set; }
    public string slug { get; set; } = "";
    public string directory { get; set; } = "";
    public string? path { get; set; }
    public string? title { get; set; }
    public string version { get; set; } = "";
    public string? share_url { get; set; }
    public long? summary_additions { get; set; }
    public long? summary_deletions { get; set; }
    public long? summary_files { get; set; }
    public string? summary_diffs { get; set; }
    public string? metadata { get; set; }
    public double cost { get; set; }
    public long tokens_input { get; set; }
    public long tokens_output { get; set; }
    public long tokens_reasoning { get; set; }
    public long tokens_cache_read { get; set; }
    public long tokens_cache_write { get; set; }
    public string? revert { get; set; }
    public string? permission { get; set; }
    public string? agent { get; set; }
    public string? model { get; set; }
    public long time_created { get; set; }
    public long time_updated { get; set; }
    public long? time_idle { get; set; }
    public long? time_viewed { get; set; }
    public string? idle_outcome { get; set; }
    public long? time_compacting { get; set; }
    public long? time_archived { get; set; }
    public long? time_suspended { get; set; }
    public long resume_attempts { get; set; }
}
internal sealed class WorkspaceRow
{
    public string id { get; set; } = "";
    public string provider { get; set; } = "";
    public string? binding { get; set; }
    public long created_at { get; set; }
    public long last_used_at { get; set; }
}
internal sealed class WorktreeRow
{
    public string project_id { get; set; } = "";
    public string directory { get; set; } = "";
    public string? strategy { get; set; }
    public long time_created { get; set; }
}
