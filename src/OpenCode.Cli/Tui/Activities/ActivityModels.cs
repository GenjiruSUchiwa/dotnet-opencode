namespace OpenCode.Cli.Tui.Activities;

using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Sessions;

/// <summary>Location comes from the API/event envelope, not the current route or the command's cwd.</summary>
public sealed record LocatedShell(ShellInfo Info, LocationRef Location);

public enum ActivityTab { Subagents, Shells }
public enum ActivityJobState { Running, Completed, Error, Cancelled }
public enum ActivityJobMode { Foreground, Background }

/// <summary>
/// Optional authoritative host read model, NOT a new HTTP contract. Leave mode/blocking null when
/// unknown. Do not derive these from titles, command names, or arbitrary ShellInfo metadata.
/// </summary>
public sealed record ActivityJobProjection(string Id, string Type, ActivityJobState State, SessionId OwnerSessionId,
    ShellId? ShellId = null, SessionId? ChildSessionId = null, ActivityJobMode? Mode = null,
    IReadOnlySet<SessionId>? BlockingSessions = null, string? Error = null);

internal sealed record FamilyEntry(SessionInfo Session, string Prefix);

internal static class ActivityProjection
{
    public static bool BelongsTo(ShellInfo shell, SessionId session) => shell.Metadata.TryGetValue("sessionID", out var owner)
        && owner.ValueKind == System.Text.Json.JsonValueKind.String && owner.GetString() == session.Value;

    public static ActivityJobProjection? ShellJob(LocatedShell shell, SessionId session, IReadOnlyList<ActivityJobProjection>? jobs) => jobs?
        .FirstOrDefault(job => job.Type == "shell" && job.ShellId == shell.Info.Id && job.OwnerSessionId == session);

    public static ActivityJobProjection? SubagentJob(SessionInfo session, IReadOnlyList<ActivityJobProjection>? jobs) => jobs?
        .FirstOrDefault(job => job.Type == "subagent" && job.ChildSessionId == session.Id && job.OwnerSessionId == session.ParentId);

    public static string JobLabel(ActivityJobProjection? job) => job is null ? "" : string.Join(" · ", new[]
    {
        job.Mode switch { ActivityJobMode.Foreground => "foreground", ActivityJobMode.Background => "background", _ => null },
        job.BlockingSessions is { Count: > 0 } ? "blocking session" : null,
        job.State switch { ActivityJobState.Error => "job failed", ActivityJobState.Completed => "job completed", ActivityJobState.Cancelled => "job cancelled", _ => null }
    }.Where(value => value is not null));

    public static string ShellState(ShellInfo info) => info.Status switch
    {
        ShellStatus.Running => "Running", ShellStatus.Killed => "Killed", ShellStatus.Timeout => "Timed out",
        ShellStatus.Exited => info.Exit is { } exit ? $"Exited ({exit})" : "Exited (code unknown)", _ => "Unknown"
    };

    public static bool Running(SessionId id, IReadOnlyDictionary<string, SessionActive>? active) =>
        active?.TryGetValue(id.Value, out var value) == true && value.Type == "running";

    public static string SessionState(SessionInfo session, IReadOnlyDictionary<string, SessionActive>? active)
    {
        if (active is null) return "Activity unavailable";
        if (Running(session.Id, active)) return "Running";
        return session.Outcome switch
        { SessionOutcome.Succeeded => "Completed", SessionOutcome.Failed => "Failed", SessionOutcome.Interrupted => "Interrupted", _ => "Idle" };
    }

    public static string? Blocked(SessionId id, IReadOnlyList<PermissionRequest>? permissions, IReadOnlyList<FormInfo>? forms) =>
        permissions?.Any(request => request.SessionId == id) == true ? "approval required"
        : forms?.Any(form => form.SessionId == id.Value) == true ? "input required" : null;

    public static string Title(SessionInfo session, DateTimeOffset now) => SessionPickerRow.From(session, false, now).Title;

    public static IReadOnlyList<FamilyEntry> Family(IReadOnlyList<SessionInfo> sessions, SessionId currentId)
    {
        var byId = sessions.ToDictionary(session => session.Id);
        if (!byId.TryGetValue(currentId, out var current)) return [];
        var seen = new HashSet<SessionId> { current.Id };
        while (current.ParentId is { } parent && byId.TryGetValue(parent, out var ancestor))
        {
            if (!seen.Add(ancestor.Id)) throw new InvalidOperationException("Session family contains a parent cycle.");
            current = ancestor;
        }
        var children = sessions.Where(session => session.ParentId is not null).GroupBy(session => session.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<FamilyEntry>();
        seen.Clear();
        Walk(current.Id, []);
        return result;

        void Walk(SessionId parent, bool[] ancestors)
        {
            if (!seen.Add(parent)) throw new InvalidOperationException("Session family contains a child cycle.");
            if (!children.TryGetValue(parent, out var group)) return;
            for (var index = 0; index < group.Length; index++)
            {
                var last = index == group.Length - 1;
                var prefix = ancestors.Length == 0 ? "" : string.Concat(ancestors.Skip(1).Select(value => value ? "   " : "│  ")) + (last ? "└─ " : "├─ ");
                result.Add(new(group[index], prefix));
                Walk(group[index].Id, [.. ancestors, last]);
            }
        }
    }

    public static string MessagePreview(IReadOnlyList<SessionMessage> messages)
    {
        var assistant = messages.OfType<AssistantMessage>().OrderByDescending(message => message.Time.Created).ThenByDescending(message => message.Id.Value, StringComparer.Ordinal).FirstOrDefault();
        if (assistant is null) return "No assistant message in the supplied history.";
        var text = string.Concat(assistant.Content.OfType<AssistantTextContent>().Select(content => content.Text));
        if (text.Length > 0) return text;
        var tool = assistant.Content.OfType<AssistantToolContent>().LastOrDefault();
        return tool is null ? "No text in the latest assistant message." : $"{tool.Name}: {tool.State switch
        { ToolStateStreaming => "receiving input", ToolStateRunning => "running", ToolStateCompleted => "completed", ToolStateError => "failed", _ => "unknown" }}";
    }

    public static string Error(Exception error) => error is SessionApiException api && api.QueryError is { } detail ? detail.Message : error.Message;
}
