namespace OpenCode.Cli.Tui.Tabs;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Source tabs.json global/cwd scopes in dotnet-channel client storage. The default remains cwd.</summary>
public sealed class SessionTabStorage(string directory, SessionTabScope scope = SessionTabScope.Cwd, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _directory = Path.GetFullPath(directory);

    public async Task<SessionTabLayout> LoadAsync(CancellationToken cancellationToken) =>
        ReadTabs(Select(await SessionUiStorage.ReadAsync("tabs", cancellationToken), false));

    public Task SaveAsync(SessionTabLayout before, SessionTabLayout after, CancellationToken cancellationToken) =>
        SessionUiStorage.MutateAsync("tabs", document =>
        {
            document["global"] ??= new JsonObject { ["tabs"] = new JsonArray(), ["unread"] = new JsonObject() };
            document["cwd"] ??= new JsonObject();
            var selected = Select(document, true)!;
            var latest = ReadTabs(selected).Tabs.ToList();
            var removed = before.Tabs.Select(tab => tab.SessionId).Except(after.Tabs.Select(tab => tab.SessionId)).ToHashSet();
            latest.RemoveAll(tab => removed.Contains(tab.SessionId));
            foreach (var tab in after.Tabs)
            {
                var previous = before.Tabs.FirstOrDefault(item => item.SessionId == tab.SessionId);
                if (previous == tab) continue;
                var index = latest.FindIndex(item => item.SessionId == tab.SessionId);
                if (index >= 0) latest[index] = tab;
                else latest.Add(tab);
            }
            // Persist drag/reopen ordering without dropping tabs another client added meanwhile.
            if (!before.Tabs.Select(tab => tab.SessionId).SequenceEqual(after.Tabs.Select(tab => tab.SessionId)))
            {
                var order = after.Tabs.Select(tab => tab.SessionId).ToHashSet();
                var ordered = new Queue<StoredSessionTab>(after.Tabs.Select(tab => latest.FirstOrDefault(item => item.SessionId == tab.SessionId)).OfType<StoredSessionTab>());
                latest = latest.Select(tab => order.Contains(tab.SessionId) ? ordered.Dequeue() : tab).ToList();
            }
            selected["tabs"] = new JsonArray(latest.Select(tab => (JsonNode)new JsonObject
            { ["sessionID"] = tab.SessionId.Value, ["title"] = tab.Title }).ToArray());
            selected["unread"] = new JsonObject();
        }, cancellationToken, _clock);

    private JsonObject? Select(JsonObject document, bool create)
    {
        if (scope == SessionTabScope.Global)
        {
            if (create && document["global"] is null) document["global"] = new JsonObject();
            return document["global"] is null ? null : document["global"] as JsonObject ?? throw new JsonException("Global tab scope must be an object.");
        }
        if (create && document["cwd"] is null) document["cwd"] = new JsonObject();
        if (document["cwd"] is null) return null;
        var cwd = document["cwd"] as JsonObject ?? throw new JsonException("Tab cwd scopes must be an object.");
        if (create && cwd[_directory] is null) cwd[_directory] = new JsonObject();
        return cwd[_directory] is null ? null : cwd[_directory] as JsonObject ?? throw new JsonException("Working-directory tab scope must be an object.");
    }

    private static SessionTabLayout ReadTabs(JsonObject? scope)
    {
        if (scope?["tabs"] is null) return SessionTabLayout.Empty;
        var array = scope["tabs"] as JsonArray ?? throw new JsonException("Stored tabs must be an array.");
        return new(array.Select(node =>
        {
            if (node is not JsonObject tab || tab["sessionID"] is not JsonValue value || !value.TryGetValue<string>(out var id))
                throw new JsonException("A stored tab requires a sessionID.");
            return new StoredSessionTab(SessionId.FromExisting(id), tab["title"]?.GetValue<string>());
        }).DistinctBy(tab => tab.SessionId).ToImmutableArray());
    }
}
