namespace OpenCode.Cli.Tui.Tabs;

using System.Collections.Immutable;
using OpenCode.Schema;

public sealed record SessionTab(Guid Key, SessionId? SessionId, string Title)
{
    public bool Preview { get; init; }
    public string? Detail { get; init; }
}
public sealed record StoredSessionTab(SessionId SessionId, string? Title);
public sealed record SessionTabActivity(bool? Busy = null, string? Title = null,
    SessionTabUnread? Unread = null, SessionTabAttention? Attention = null, bool Renaming = false, long PromptPulse = 0)
{
    public bool Running => Busy == true && Attention is null;
    public bool Complete => Unread == SessionTabUnread.Activity && Busy != true;

    public static SessionTabActivity From(SessionInfo root, IEnumerable<SessionId> family,
        IReadOnlySet<SessionId> running, IReadOnlySet<SessionId> pending,
        IReadOnlySet<SessionId> permissions, IReadOnlySet<SessionId> forms, bool renaming = false, long promptPulse = 0)
    {
        var members = family.Append(root.Id).Distinct().ToArray();
        var unread = root.Time.Idle is { } idle && (root.Time.Viewed is null || idle > root.Time.Viewed);
        return new(members.Any(id => running.Contains(id) || pending.Contains(id)), root.Title,
            unread ? root.Outcome == SessionOutcome.Failed ? SessionTabUnread.Error : SessionTabUnread.Activity : null,
            members.Any(permissions.Contains) ? SessionTabAttention.Permission : members.Any(forms.Contains) ? SessionTabAttention.Question : null,
            renaming, promptPulse);
    }
}
public sealed record SessionTabLayout(ImmutableArray<StoredSessionTab> Tabs)
{
    public static SessionTabLayout Empty { get; } = new([]);
}

/// <summary>Immutable view identities; closing a tab is not a server-session operation.</summary>
public sealed record SessionTabState(ImmutableArray<SessionTab> Tabs, Guid Selected, ImmutableArray<SessionTab> Closed)
{
    public ImmutableArray<Guid> History { get; init; } = [];
    public int HistoryIndex { get; init; } = -1;
    public ImmutableDictionary<Guid, int> ClosedPositions { get; init; } = ImmutableDictionary<Guid, int>.Empty;
    public static SessionTabState Create()
    {
        var home = new SessionTab(Guid.NewGuid(), null, "New session");
        return new([home], home.Key, []);
    }

    public SessionTab Current => Tabs.First(tab => tab.Key == Selected);
    public SessionTabLayout Persisted => new(Tabs.Where(tab => tab.SessionId is not null)
        .Select(tab => new StoredSessionTab(tab.SessionId!.Value, tab.Title)).ToImmutableArray());

    public SessionTabState Register(SessionId id, string title)
    {
        var duplicate = Tabs.FirstOrDefault(tab => tab.SessionId == id && tab.Key != Selected);
        var tabs = duplicate is null ? Tabs : Tabs.Remove(duplicate);
        return (this with { Tabs = tabs.Replace(Current, Current with { SessionId = id, Title = title }) }).Select(Selected);
    }

    public SessionTabState Select(Guid key)
    {
        if (!Tabs.Any(tab => tab.Key == key)) throw new ArgumentException("The tab is not open.", nameof(key));
        if (Tabs.First(tab => tab.Key == key).SessionId is null) return this with { Selected = key };
        if (HistoryIndex >= 0 && HistoryIndex < History.Length && History[HistoryIndex] == key) return this with { Selected = key };
        var history = History.Take(HistoryIndex + 1).Append(key).TakeLast(100).ToImmutableArray();
        return this with { Selected = key, History = history, HistoryIndex = history.Length - 1 };
    }

    public SessionTabState Close(Guid key)
    {
        var tab = Tabs.FirstOrDefault(tab => tab.Key == key);
        if (tab is null) return this;
        var index = Tabs.IndexOf(tab);
        var remaining = Tabs.Remove(tab);
        if (remaining.IsEmpty) remaining = [new SessionTab(Guid.NewGuid(), null, "New session")];
        var prior = History.Take(HistoryIndex + (tab.SessionId is null ? 1 : 0)).Reverse()
            .FirstOrDefault(candidate => candidate != key && remaining.Any(item => item.Key == candidate));
        var closed = tab.SessionId is null ? Closed : Closed.Where(item => item.SessionId != tab.SessionId).Append(tab).TakeLast(10).ToImmutableArray();
        return this with
        {
            Tabs = remaining,
            Selected = Selected == key ? prior != Guid.Empty ? prior : remaining[Math.Min(index, remaining.Length - 1)].Key : Selected,
            Closed = closed,
            HistoryIndex = Selected == key && prior != Guid.Empty ? History.LastIndexOf(prior) : HistoryIndex,
            ClosedPositions = ClosedPositions.SetItem(key, index).Where(pair => closed.Any(item => item.Key == pair.Key)).ToImmutableDictionary()
        };
    }

    public SessionTabState Move(Guid key, int index)
    {
        var tab = Tabs.FirstOrDefault(tab => tab.Key == key);
        if (tab is null || tab.SessionId is null) return this;
        var target = Math.Clamp(index, 0, Tabs.Count(item => item.SessionId is not null) - 1);
        if (Tabs.IndexOf(tab) == target) return this;
        return this with { Tabs = Tabs.Remove(tab).Insert(target, tab with { Preview = false }) };
    }

    public SessionTabState Promote(Guid key) => this with { Tabs = Tabs.Select(tab => tab.Key == key ? tab with { Preview = false } : tab).ToImmutableArray() };

    public SessionTabState Reopen()
    {
        var remaining = Closed;
        while (!remaining.IsEmpty)
        {
            var tab = remaining[^1];
            remaining = remaining.RemoveAt(remaining.Length - 1);
            if (Tabs.Any(item => item.SessionId == tab.SessionId)) continue;
            return (this with { Tabs = Tabs.Insert(Math.Min(ClosedPositions.GetValueOrDefault(tab.Key, Tabs.Length), Tabs.Length), tab with { Preview = false }),
                Closed = remaining, ClosedPositions = ClosedPositions.Remove(tab.Key) }).Select(tab.Key);
        }
        return this with { Closed = remaining };
    }

    public SessionTab? Cycle(int direction, Func<SessionTab, bool>? matches = null)
    {
        var tabs = Tabs.Where(tab => tab.SessionId is not null).ToArray();
        if (tabs.Length == 0) return null;
        var index = Array.FindIndex(tabs, tab => tab.Key == Selected);
        var start = index == -1 ? direction > 0 ? -1 : 0 : index;
        return Enumerable.Range(1, tabs.Length).Select(offset => tabs[(start + Math.Sign(direction) * offset + tabs.Length * 2) % tabs.Length])
            .FirstOrDefault(tab => matches?.Invoke(tab) ?? true);
    }

    public SessionTabState RemoveDeleted(SessionId id)
    {
        var tab = Tabs.FirstOrDefault(item => item.SessionId == id);
        var state = tab is null ? this : Close(tab.Key);
        var closed = state.Closed.Where(item => item.SessionId != id).ToImmutableArray();
        return state with { Closed = closed, ClosedPositions = state.ClosedPositions.Where(pair => closed.Any(item => item.Key == pair.Key)).ToImmutableDictionary() };
    }

    public SessionTab? CycleUnread(int direction, IReadOnlyDictionary<SessionId, SessionTabActivity> activity) => Cycle(direction,
        tab => tab.SessionId is { } id && activity.TryGetValue(id, out var status) && (status.Unread is not null || status.Attention is not null));

    public SessionTab? SelectIndex(int index) => Tabs.Where(tab => tab.SessionId is not null).ElementAtOrDefault(index);

    public SessionTabState Open(SessionId id, string title, bool preview = false)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.SessionId == id);
        if (existing is not null) return (this with { Tabs = Tabs.Replace(existing, existing with { Title = title }) }).Select(existing.Key);
        var tab = new SessionTab(Guid.NewGuid(), id, title) { Preview = preview };
        var replaced = preview ? Tabs.FirstOrDefault(item => item.Preview) : null;
        return (this with { Tabs = replaced is null ? Tabs.Add(tab) : Tabs.Replace(replaced, tab) }).Select(tab.Key);
    }

    public SessionTabState NavigateHistory(int direction)
    {
        var indices = direction < 0 ? Enumerable.Range(0, Math.Max(0, HistoryIndex)).Reverse()
            : Enumerable.Range(Math.Max(0, HistoryIndex + 1), Math.Max(0, History.Length - HistoryIndex - 1));
        var target = indices.FirstOrDefault(index => History[index] != Selected && Tabs.Any(tab => tab.Key == History[index]), -1);
        return target < 0 ? this : this with { Selected = History[target], HistoryIndex = target };
    }

    public SessionTabState Normalize(IReadOnlyDictionary<SessionId, SessionInfo> sessions)
    {
        var selected = Selected;
        var result = new List<SessionTab>();
        foreach (var tab in Tabs)
        {
            if (tab.SessionId is not { } id) { result.Add(tab); continue; }
            var seen = new HashSet<SessionId>();
            while (sessions.TryGetValue(id, out var session) && session.ParentId is { } parent && seen.Add(id)) id = parent;
            var existing = result.FirstOrDefault(item => item.SessionId == id);
            if (existing is not null) { if (selected == tab.Key) selected = existing.Key; continue; }
            result.Add(tab with { SessionId = id, Title = sessions.GetValueOrDefault(id)?.Title ?? tab.Title });
        }
        var normalized = result.ToImmutableArray();
        return selected == Selected && normalized.SequenceEqual(Tabs) ? this : this with { Tabs = normalized, Selected = selected };
    }
}
