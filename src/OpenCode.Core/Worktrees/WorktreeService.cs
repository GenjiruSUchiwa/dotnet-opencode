namespace OpenCode.Core.Worktrees;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Schema;

public sealed record WorktreeRefreshResult(IReadOnlyList<string> Updated, IReadOnlyList<string> Removed);

/// <summary>Source local strategy lifecycle. Filesystem/Git effects are not a database transaction or clustered lock.</summary>
public sealed class WorktreeService(IDatabase database, Action<OpenCodeEvent>? publish = null)
{
    private readonly WorktreeStore _store = new(database);
    private readonly Dictionary<string, IWorktreeStrategy> _strategies = new(StringComparer.Ordinal) { ["git"] = new GitWorktreeStrategy() };
    private readonly Lock _registry = new();

    public void Register(IWorktreeStrategy strategy)
    {
        var id = WorktreeTrimmedStringConverter.Normalize(strategy.Id);
        lock (_registry) if (!_strategies.TryAdd(id, strategy)) throw new InvalidOperationException($"Worktree strategy is already registered: {id}");
    }

    public Task<IReadOnlyList<WorktreeDirectory>> ListAsync(ProjectId project, CancellationToken ct = default) => _store.ListAsync(project, ct);

    public async Task<WorktreeInfo> CreateAsync(WorktreeCreateInput input, CancellationToken ct = default)
    {
        var id = WorktreeTrimmedStringConverter.Normalize(input.Strategy);
        var strategy = Strategy(id);
        var source = input.From ?? await _store.PrimaryAsync(input.ProjectId, ct)
            ?? throw new WorktreeException($"Worktree source not found for project: {input.ProjectId.Value}");
        source = WorktreePaths.Canonical(source);
        if (await _store.FindAsync(input.ProjectId, source, ct) is null) throw new WorktreeException($"Worktree source not found: {source}");
        await RequirePluginsAsync(source, ct);
        Directory.CreateDirectory(input.Directory);
        var name = input.Name ?? WorktreePaths.Slug();
        var suffix = 1;
        var directory = WorktreePaths.Join(input.Directory, name);
        while (WorktreePaths.Exists(directory))
        {
            if (++suffix > 10) throw new WorktreeException($"Worktree destination already exists: {directory}");
            directory = WorktreePaths.Join(input.Directory, name + "-" + suffix);
        }
        var result = await strategy.CreateAsync(source, directory,
            input.Branch is null ? null : WorktreeTrimmedStringConverter.Normalize(input.Branch), ct);
        if (await _store.PutAsync(input.ProjectId, result.Directory, id, ct)) Changed(input.ProjectId);
        var startup = Trim(await _store.StartupAsync(input.ProjectId, ct) ?? "");
        if (startup.Length > 0) await RunStartupAsync(startup, source, result.Directory, ct);
        return result;
    }

    public async Task RemoveAsync(WorktreeRemoveInput input, CancellationToken ct = default)
    {
        var directory = WorktreePaths.Canonical(input.Directory);
        var stored = await _store.FindAsync(input.ProjectId, directory, ct);
        if (string.IsNullOrEmpty(stored?.Strategy)) throw new WorktreeException($"Invalid worktree directory: {directory}");
        var strategy = Strategy(stored.Strategy);
        await RequirePluginsAsync(directory, ct);
        // Source Worktree.remove does not interrupt Sessions, delete their data,
        // or force-dispose Locations. Git decides whether --force is required.
        await strategy.RemoveAsync(directory, input.Force, ct);
        if (await _store.RemoveAsync(input.ProjectId, directory, ct)) Changed(input.ProjectId);
    }

    public async Task<WorktreeRefreshResult> RefreshAsync(ProjectId project, CancellationToken ct = default)
    {
        var stored = await _store.ListAsync(project, ct);
        var checkedRows = stored.Select(row => (Row: row, Exists: Directory.Exists(row.Directory))).ToArray();
        KeyValuePair<string, IWorktreeStrategy>[] strategies;
        lock (_registry) strategies = _strategies.ToArray();
        var discovered = new Dictionary<string, WorktreeDirectory>(StringComparer.Ordinal);
        foreach (var source in checkedRows.Where(row => row.Row.Strategy is null && row.Exists))
        {
            await RequirePluginsAsync(source.Row.Directory, ct);
            foreach (var strategy in strategies)
            {
                IReadOnlyList<WorktreeListEntry> entries;
                try { entries = await strategy.Value.ListAsync(source.Row.Directory, ct); }
                catch (WorktreeException error) when (error.Message.StartsWith("Worktree directory unavailable: ", StringComparison.Ordinal)) { continue; }
                foreach (var entry in entries) discovered[entry.Directory] = new(entry.Directory, entry.Root ? null : strategy.Key);
            }
        }
        var changes = await database.RunInTransactionAsync(async (connection, transaction) =>
        {
            var updated = new List<string>();
            var removed = new List<string>();
            foreach (var row in discovered.Values)
                if (await WorktreeStore.PutAsync(connection, transaction, project, row.Directory, row.Strategy, ct, database.Clock)) updated.Add(row.Directory);
            foreach (var row in checkedRows.Where(row => !row.Exists))
                if (await WorktreeStore.RemoveAsync(connection, transaction, project, row.Row.Directory, ct)) removed.Add(row.Row.Directory);
            return new WorktreeRefreshResult(updated, removed);
        }, ct);
        if (changes.Updated.Count > 0 || changes.Removed.Count > 0) Changed(project);
        return changes;
    }

    private IWorktreeStrategy Strategy(string id)
    {
        lock (_registry) return _strategies.TryGetValue(id, out var strategy) ? strategy : throw new WorktreeException($"Worktree strategy unavailable: {id}");
    }
    private void Changed(ProjectId project)
    {
        var value = WorktreeEventDefinitions.Updated.Create(EventId.Create(), database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), new(project));
        if (publish is not null) publish(value);
        else SessionEvents.Notify(value, true);
    }

    internal static async Task RequirePluginsAsync(string directory, CancellationToken ct)
    {
        foreach (var source in (await ConfigLoader.LoadSnapshotAsync(directory, ct)).Sources)
        {
            if (source is ConfigSource.Document document)
                foreach (var name in new[] { "plugin", "plugins" })
                    if (document.Info[name] is { } value && value is not JsonArray { Count: 0 } && value is not JsonObject { Count: 0 })
                        throw new WorktreeException("Configured worktree/plugin hooks require the native plugin runtime.");
            if (source is ConfigSource.Discovery { Entry: ConfigDirectory root })
                foreach (var name in new[] { "plugin", "plugins" })
                {
                    var path = Path.Combine(root.Path, name);
                    if (WorktreePaths.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        throw new WorktreeException("Discovered worktree/plugin hooks require the native plugin runtime.");
                }
        }
    }

    private static async Task RunStartupAsync(string command, string source, string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var input = File.OpenNullHandle();
        var windows = OperatingSystem.IsWindows();
        var description = windows ? command : "bash -lc " + command;
        var start = new ProcessStartInfo(windows ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "bash")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, InheritedHandles = [],
            StandardInputHandle = input, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // The configured startup command is intentionally shell syntax. This raw
        // Windows tail matches Node shell:true; Git commands never use this path.
        if (windows) start.Arguments = "/d /s /c \"" + command + "\"";
        else { start.ArgumentList.Add("-lc"); start.ArgumentList.Add(command); }
        start.Environment["OPENCODE_WORKTREE_BASE"] = source;
        start.Environment["OPENCODE_WORKTREE_PATH"] = directory;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        try
        {
            var result = await Process.RunAndCaptureTextAsync(start, ct);
            ct.ThrowIfCancellationRequested();
            if (result.ExitStatus.Canceled) throw new OperationCanceledException(ct);
            if (result.ExitStatus.ExitCode == 0 && result.ExitStatus.Signal is null) return;
            var detail = result.StandardError.Trim();
            throw new WorktreeException($"Command failed{(result.ExitStatus.ExitCode is { } code ? $" (exit {code})" : "")}: {description}{(detail.Length == 0 ? "" : ": " + detail)}");
        }
        catch (Win32Exception error) { throw new WorktreeException($"Command failed: {description}: {error.Message}", inner: error); }
    }

    private static string Trim(string value) => value.Trim("\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF".ToCharArray());
}
