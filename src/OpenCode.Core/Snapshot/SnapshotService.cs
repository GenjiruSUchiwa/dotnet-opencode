namespace OpenCode.Core.Snapshot;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Schema;

public sealed class SnapshotException(string operation, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Operation { get; } = operation;
}

/// <summary>Host-owned Location scopes with shared per-repository index locks, not another Session coordinator.</summary>
public sealed class SessionSnapshotLocations(IDatabase database, string? dataDirectory = null, string gitExecutable = "git")
{
    private readonly ConcurrentDictionary<LocationRef, SnapshotService> _locations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Model execution follows Snapshot.capture's best-effort acquisition policy.</summary>
    public async Task<SnapshotService?> TryGetAsync(LocationRef location, CancellationToken ct = default)
    {
        try { return await GetAsync(location, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Trace.TraceWarning("Snapshot location unavailable: {0}", error.Message);
            return null;
        }
    }

    public async Task<SnapshotService> GetAsync(LocationRef location, CancellationToken ct = default)
    {
        var key = PermissionLocationMap.Canonical(location);
        if (_locations.TryGetValue(key, out var existing)) return existing;
        var info = await CatalogLocation.ResolveAsync(database, key.Directory, key.WorkspaceId?.Value, ct).ConfigureAwait(false);
        var storage = Path.Combine(dataDirectory ?? ConfigLoader.GetDefaultDataDirectory(), "snapshot", info.Project.Id.Value,
            Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(info.Project.Directory))));
        return _locations.GetOrAdd(key, _ => new SnapshotService(info with { Directory = key.Directory }, storage,
            gitExecutable, _locks.GetOrAdd(storage, _ => new SemaphoreSlim(1, 1))));
    }
}

/// <summary>Content-addressed Git trees in isolated storage. Never changes the user's Git index or HEAD.</summary>
public sealed class SnapshotService
{
    private readonly LocationInfo _location;
    private readonly string _storage;
    private readonly string _git;
    private readonly SemaphoreSlim _gate;
    private string? _sourceGit;
    private string? _worktree;

    internal SnapshotService(LocationInfo location, string storage, string git, SemaphoreSlim gate)
    { _location = location; _storage = storage; _git = git; _gate = gate; }

    public async Task<SnapshotId?> CaptureAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Enabled()) return null;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await InitializeAsync(ct).ConfigureAwait(false);
                var scope = Relative(_location.Directory);
                var tracked = Paths(await RunAsync("capture", _storage, ["diff-files", "--name-only", "-z", "--", scope], ct).ConfigureAwait(false));
                var untracked = Paths(await RunAsync("capture", _storage, ["ls-files", "--others", "--exclude-standard", "-z", "--", scope], ct).ConfigureAwait(false));
                var candidates = tracked.Concat(untracked).Distinct(StringComparer.Ordinal).ToArray();
                var excluded = await IgnoredAsync("capture", candidates, ct).ConfigureAwait(false);
                var oversized = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in untracked.Where(file => !excluded.Contains(file)))
                {
                    try { if (new FileInfo(Absolute(file)).Length > 2 * 1024 * 1024) oversized.Add(file); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                var remove = excluded.Concat(oversized).Distinct(StringComparer.Ordinal).ToArray();
                if (remove.Length > 0)
                    await RunAsync("capture", _storage, ["rm", "--cached", "-f", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, string.Join('\0', remove) + "\0").ConfigureAwait(false);
                var stage = candidates.Where(file => !excluded.Contains(file) && !oversized.Contains(file)).ToArray();
                if (stage.Length > 0)
                    await RunAsync("capture", _storage, ["add", "--all", "--sparse", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, string.Join('\0', stage) + "\0").ConfigureAwait(false);
                return SnapshotId.FromExisting((await RunAsync("capture", _storage, ["write-tree"], ct).ConfigureAwait(false)).Trim());
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Trace.TraceWarning("Snapshot capture failed: {0}", error.Message);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> FilesAsync(SnapshotId from, SnapshotId to, CancellationToken ct = default)
    {
        RequireTree(from); RequireTree(to);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var files = Paths(await RunAsync("files", _storage, ["diff", "--name-only", "--no-renames", "-z", from.Value, to.Value], ct).ConfigureAwait(false));
            var ignored = await IgnoredAsync("files", files, ct).ConfigureAwait(false);
            return files.Where(file => !ignored.Contains(file)).ToArray();
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && error is not SnapshotException { Operation: "files" })
        { throw new SnapshotException("files", error.Message, error); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<FileDiffInfo>> DiffAsync(SnapshotId from, SnapshotId to,
        IReadOnlyList<string>? paths = null, int context = 3, CancellationToken ct = default)
    {
        RequireTree(from); RequireTree(to);
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var changed = Paths(await RunAsync("diff", _storage, ["diff", "--name-only", "--no-renames", "-z", from.Value, to.Value], ct).ConfigureAwait(false));
            var ignored = await IgnoredAsync("diff", changed, ct).ConfigureAwait(false);
            var result = new List<FileDiffInfo>();
            foreach (var file in (paths ?? changed).Where(file => !ignored.Contains(file)))
            {
                Absolute(file);
                var status = (await RunAsync("diff", _storage, ["diff", "--name-status", "--no-renames", from.Value, to.Value, "--", file], ct).ConfigureAwait(false)).Trim();
                var stats = (await RunAsync("diff", _storage, ["diff", "--numstat", "--no-renames", from.Value, to.Value, "--", file], ct).ConfigureAwait(false)).Split('\t');
                var binary = stats[0] == "-" || stats.ElementAtOrDefault(1) == "-";
                var patch = binary ? "" : await RunAsync("diff", _storage, ["diff", $"--unified={context}", "--no-renames", from.Value, to.Value, "--", file], ct).ConfigureAwait(false);
                result.Add(new(file, patch, binary ? 0 : Count(stats[0]), binary ? 0 : Count(stats.ElementAtOrDefault(1)),
                    status.StartsWith('A') ? FileDiffStatus.Added : status.StartsWith('D') ? FileDiffStatus.Deleted : FileDiffStatus.Modified));
            }
            return result;
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && error is not SnapshotException { Operation: "diff" })
        { throw new SnapshotException("diff", error.Message, error); }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(IReadOnlyDictionary<string, SnapshotId> files, CancellationToken ct = default)
    {
        if (!Enabled()) throw new SnapshotException("restore", "Snapshots are disabled");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            // Validate the complete map before the first filesystem mutation.
            foreach (var pair in files) { Absolute(pair.Key); RequireTree(pair.Value); }
            foreach (var pair in files)
            {
                var entry = (await RunAsync("restore", _storage, ["ls-tree", "-z", pair.Value.Value, "--", pair.Key], ct).ConfigureAwait(false)).TrimEnd('\0');
                if (entry.Length > 0)
                {
                    if (!Regex.IsMatch(entry, @"^\d+\s+\w+\s+[0-9a-f]+\t", RegexOptions.NonBacktracking)) throw new SnapshotException("restore", "Invalid tree entry: " + pair.Key);
                    await RunAsync("restore", _storage, ["checkout", pair.Value.Value, "--", pair.Key], ct).ConfigureAwait(false);
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                var target = Absolute(pair.Key);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else File.Delete(target);
            }
        }
        catch (Exception error) when (error is IOException and not SnapshotException or UnauthorizedAccessException)
        { throw new SnapshotException("restore", "Failed to restore selected snapshot paths", error); }
        finally { _gate.Release(); }
    }

    private bool Enabled()
    {
        if (!Path.Exists(Path.Combine(_location.Project.Directory, ".git"))) return false;
        var value = ConfigLoader.LoadDocument(directory: _location.Directory)["snapshots"];
        return value is null || value.GetValue<bool>();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        if (_sourceGit is not null) return;
        var discovery = (await RunAsync("capture", null, ["rev-parse", "--git-dir", "--git-common-dir", "--show-toplevel"], ct).ConfigureAwait(false)).TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (discovery.Length != 3) throw new SnapshotException("capture", "Project Git discovery failed");
        var source = Path.GetFullPath(discovery[0], _location.Project.Directory);
        var common = Path.GetFullPath(discovery[1], _location.Project.Directory);
        _worktree = Path.GetFullPath(discovery[2], _location.Project.Directory);
        Relative(_location.Directory);
        Directory.CreateDirectory(_storage);
        if (!File.Exists(Path.Combine(_storage, "HEAD")))
        {
            await RunAsync("capture", _storage, ["init"], ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(_storage, "opencode.gitconfig"), "[core]\n\tautocrlf = false\n\tlongpaths = true\n\tsymlinks = true\n\tfsmonitor = false\n\tuntrackedCache = true\n[feature]\n\tmanyFiles = true\n[index]\n\tversion = 4\n\tthreads = true\n", ct).ConfigureAwait(false);
            await File.AppendAllTextAsync(Path.Combine(_storage, "config"), "\n[include]\n\tpath = opencode.gitconfig\n", ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(_storage, "objects", "info"));
            await File.WriteAllTextAsync(Path.Combine(_storage, "objects", "info", "alternates"), Path.Combine(common, "objects") + "\n", ct).ConfigureAwait(false);
            try { File.Copy(Path.Combine(source, "index"), Path.Combine(_storage, "index"), overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        _sourceGit = source;
    }

    private async Task<HashSet<string>> IgnoredAsync(string operation, IReadOnlyList<string> paths, CancellationToken ct) => paths.Count == 0 ? [] :
        Paths(await RunAsync(operation, _sourceGit, ["check-ignore", "--no-index", "--stdin", "-z"], ct, string.Join('\0', paths) + "\0", allowOne: true).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    private async Task<string> RunAsync(string operation, string? gitDirectory, IReadOnlyList<string> arguments, CancellationToken ct, string? stdin = null, bool allowOne = false)
    {
        ct.ThrowIfCancellationRequested();
        // The pinned .NET process API owns and reaps only this child. File-backed stdin
        // carries NUL pathspecs without shell quoting or a detached writer task.
        var temporary = Path.Combine(Path.GetTempPath(), "opencode-snapshot-" + Guid.NewGuid().ToString("N"));
        var input = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        await using var inputLifetime = input.ConfigureAwait(false);
        if (stdin is not null) { await input.WriteAsync(Encoding.UTF8.GetBytes(stdin), ct).ConfigureAwait(false); await input.FlushAsync(ct).ConfigureAwait(false); input.Position = 0; }
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = _worktree ?? _location.Project.Directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputHandle = input.SafeFileHandle,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, InheritedHandles = []
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) start.KillOnParentExit = true;
        if (gitDirectory is not null)
            foreach (var argument in new[] { "--git-dir", gitDirectory, "--work-tree", _worktree ?? _location.Project.Directory }) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            var result = await Process.RunAndCaptureTextAsync(start, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ExitStatus.Canceled) throw new OperationCanceledException(ct);
            if (result.ExitStatus.Signal is not null || result.ExitStatus.ExitCode != 0 && !(allowOne && result.ExitStatus.ExitCode == 1))
                throw new SnapshotException(operation, result.StandardError.Trim().Length > 0 ? result.StandardError.Trim() : $"Git {operation} failed");
            return result.StandardOutput;
        }
        catch (Win32Exception error) { throw new SnapshotException(operation, "Snapshot Git executable is unavailable", error); }
    }

    private string Relative(string absolute)
    {
        var path = Path.GetRelativePath(_worktree!, absolute).Replace('\\', '/');
        if (path == ".." || path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path)) throw new SnapshotException("capture", "Location is outside the project");
        return path;
    }
    private string Absolute(string file)
    {
        var absolute = Path.GetFullPath(file, _worktree!);
        var relative = Path.GetRelativePath(_worktree!, absolute);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new SnapshotException("restore", "Path escapes the project or names its root: " + file);
        return absolute;
    }
    private static void RequireTree(SnapshotId id)
    {
        if (!id.IsInitialized() || !Regex.IsMatch(id.Value, @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.NonBacktracking)) throw new ArgumentException("Snapshot ID must be a content-addressed Git tree.", nameof(id));
    }
    private static string[] Paths(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static int Count(string? value) => string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
}
