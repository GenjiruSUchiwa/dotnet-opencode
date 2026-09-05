namespace OpenCode.Core.Session;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>SessionInstructions.load: temporary claims plus the model-visible synthetic metadata ledger.</summary>
internal sealed class ReadInstructionLoader(IDatabase database)
{
    private readonly Lock _sync = new();
    private readonly HashSet<(SessionId Session, string Path)> _inFlight = [];

    internal async Task LoadAsync(SessionId sessionId, IReadOnlyList<string> paths, string projectRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = paths.ToArray();
        if (!Path.IsPathFullyQualified(projectRoot) || candidates.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Read instruction paths and project root must be resolved absolute paths.");
        string[] claimed;
        lock (_sync) claimed = candidates.Where(path => _inFlight.Add((sessionId, path))).ToArray();
        if (claimed.Length == 0) return;
        try
        {
            var injected = await new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
            {
                var rows = await SessionQueries.Context(transaction.Db, sessionId.Value).Where(row => row.type == "synthetic")
                    .OrderBy(row => row.seq).Select(row => row.data).ToListAsync(token);
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    using var document = JsonDocument.Parse(row);
                    if (!document.RootElement.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                        !metadata.TryGetProperty("instruction", out var instruction) || instruction.ValueKind != JsonValueKind.Object ||
                        !instruction.TryGetProperty("paths", out var loaded) || loaded.ValueKind != JsonValueKind.Array ||
                        loaded.EnumerateArray().Any(path => path.ValueKind != JsonValueKind.String)) continue;
                    foreach (var path in loaded.EnumerateArray()) result.Add(path.GetString()!);
                }
                return result;
            }, ct);
            var files = new List<(string Path, string Content)>();
            foreach (var path in claimed.Where(path => !injected.Contains(path)))
            {
                ct.ThrowIfCancellationRequested();
                try { files.Add((path, await File.ReadAllTextAsync(path, ct))); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (files.Count == 0) return;
            string Describe(string path)
            {
                var relative = Path.GetRelativePath(projectRoot, path);
                return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    ? path : relative == "." ? "" : relative;
            }
            await new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
            {
                if (!await transaction.Db.Sessions.AnyAsync(row => row.id == sessionId.Value, token)) throw new InvalidOperationException("Session not found.");
                return await transaction.AppendAsync(SessionSyntheticProjector.Definition, new SessionSyntheticData(sessionId,
                    string.Join("\n\n", files.Select(file => $"Instructions from: {file.Path}\n{file.Content}")),
                    "Loaded " + string.Join(", ", files.Select(file => Describe(file.Path))),
                    new Dictionary<string, JsonElement> { ["instruction"] = JsonSerializer.SerializeToElement(new { paths = files.Select(file => file.Path).ToArray() }) }), token);
            }, ct);
        }
        finally { lock (_sync) foreach (var path in claimed) _inFlight.Remove((sessionId, path)); }
    }

}
