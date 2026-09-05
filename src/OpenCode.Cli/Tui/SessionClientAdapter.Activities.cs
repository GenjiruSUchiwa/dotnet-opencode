namespace OpenCode.Cli.Tui;

using System.Collections.Immutable;
using System.Text.Json;
using OpenCode.Client;
using OpenCode.Cli.Tui.Activities;
using OpenCode.Schema;
using OpenCode.Protocol.Groups;

public sealed record ShellActivityRefresh(LocationRef Location, ShellId Id, long Revision);
public sealed record ActivityFeedSnapshot(long Revision, long SessionsRevision, long ShellEventsRevision, IReadOnlyList<LocatedShell> Shells,
    IReadOnlyList<ShellActivityRefresh> Refresh, string? Error);

public sealed partial class SessionClientAdapter
{
    private readonly Dictionary<(LocationRef Location, ShellId Id), LocatedShell> _activityShells = [];
    private readonly Dictionary<(LocationRef Location, ShellId Id), long> _activityShellVersions = [];
    private readonly Dictionary<(LocationRef Location, ShellId Id), long> _activityShellRefresh = [];
    private long _activityRevision;
    private long _activitySessionsRevision;
    private long _activityShellEventsRevision;
    private ActivityFeedSnapshot _activityFeed = new(0, 0, 0, [], [], null);
    public ActivityFeedSnapshot ActivityFeed { get { lock (_gate) return _activityFeed; } }

    public async Task<IReadOnlyList<SessionInfo>> LoadActivityFamilyAsync(SessionId id, CancellationToken cancellationToken)
    {
        var current = (await RequestAsync(token => _client.GetAsync(id, token), "activity Session", cancellationToken)).Data;
        var family = new Dictionary<SessionId, SessionInfo> { [current.Id] = current };
        while (current.ParentId is { } parent)
        {
            if (family.ContainsKey(parent)) throw new InvalidOperationException("Session family contains a parent cycle.");
            current = (await RequestAsync(token => _client.GetAsync(parent, token), "activity ancestor", cancellationToken)).Data;
            family.Add(current.Id, current);
        }
        var pending = new Queue<SessionId>();
        pending.Enqueue(current.Id);
        var visited = new HashSet<SessionId>();
        while (pending.TryDequeue(out var parent))
        {
            if (!visited.Add(parent)) throw new InvalidOperationException("Session family contains a child cycle.");
            string? cursor = null;
            var cursors = new HashSet<string>();
            do
            {
                var page = await RequestAsync(token => _client.ListAsync(new SessionListQuery
                { ParentId = parent, Limit = 100, Cursor = cursor }, token), "activity children", cancellationToken);
                foreach (var child in page.Data)
                {
                    if (child.ParentId != parent) throw new InvalidOperationException("Session family returned an unrelated child.");
                    family[child.Id] = child;
                    pending.Enqueue(child.Id);
                }
                cursor = page.Cursor.Next;
                if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("Session family repeated a page cursor.");
            } while (cursor is not null);
        }
        return family.Values.ToArray();
    }

    private void ObserveActivityEvent(ServerEventEnvelope item)
    {
        lock (_gate)
        {
            if (item.Type is "session.created" or "session.deleted" or "session.renamed" or "session.execution.started"
                or "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted")
            {
                _activitySessionsRevision++;
                PublishActivityFeed();
                return;
            }
            if (item.Type is not ("shell.created" or "shell.exited" or "shell.deleted" or "session.shell.started" or "session.shell.ended")) return;
            _activityShellEventsRevision++;
            if (item.Location is not { } location) { PublishActivityFeed("Shell event has no execution location."); return; }
            try
            {
                if (item.Type is "shell.created" or "session.shell.started" or "session.shell.ended")
                {
                    var info = (item.Type == "shell.created" ? item.Data.Deserialize(OpenCodeJsonContext.Default.ShellInfoEventData)?.Info
                        : item.Data.GetProperty("shell").Deserialize(OpenCodeJsonContext.Default.ShellInfo))
                        ?? throw new JsonException("Shell-created event has no ShellInfo.");
                    var key = (location, info.Id);
                    _activityShellVersions[key] = ++_activityRevision;
                    if (!_activityShells.TryGetValue(key, out var retained) || retained.Info.Status == ShellStatus.Running || info.Status != ShellStatus.Running)
                        _activityShells[key] = new(info, location);
                    _activityShellRefresh.Remove(key);
                }
                else
                {
                    var id = ShellId.FromExisting(item.Data.GetProperty("id").GetString()!);
                    var key = (location, id);
                    _activityShellVersions[key] = ++_activityRevision;
                    if (item.Type == "shell.deleted") { _activityShells.Remove(key); _activityShellRefresh.Remove(key); }
                    else _activityShellRefresh[key] = _activityRevision;
                }
                PublishActivityFeed();
            }
            catch (JsonException error) { PublishActivityFeed(error.Message); }
        }
    }

    public void MergeActivityShell(LocatedShell shell, long? expectedRevision = null)
    {
        lock (_gate)
        {
            var key = (shell.Location, shell.Info.Id);
            if (expectedRevision is { } version && _activityShellVersions.GetValueOrDefault(key) > version) return;
            _activityShellVersions[key] = ++_activityRevision;
            _activityShells[key] = shell;
            _activityShellRefresh.Remove(key);
            PublishActivityFeed();
        }
    }

    public void RemoveActivityShell(LocationRef location, ShellId id, long? expectedRevision = null)
    {
        lock (_gate)
        {
            var key = (location, id);
            if (expectedRevision is { } version && _activityShellVersions.GetValueOrDefault(key) > version) return;
            _activityShellVersions[key] = ++_activityRevision;
            _activityShells.Remove(key);
            _activityShellRefresh.Remove(key);
            PublishActivityFeed();
        }
    }

    private void PublishActivityFeed(string? error = null) => _activityFeed = new(++_activityRevision, _activitySessionsRevision, _activityShellEventsRevision,
        _activityShells.Values.ToImmutableArray(), _activityShellRefresh.Select(pair => new ShellActivityRefresh(pair.Key.Location, pair.Key.Id, pair.Value)).ToArray(), error);
}
