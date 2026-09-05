namespace OpenCode.Core.Vcs;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Schema;

public sealed class VcsUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Read-only Git VCS adapter over an already resolved Location. Does not create projects, indexes, or worktrees.</summary>
public sealed class LocalVcs
{
    public const int PatchContext = int.MaxValue;
    public const int PatchBytes = 10_000_000;
    private static readonly string[] Configuration = ["--no-optional-locks", "-c", "core.autocrlf=false", "-c", "core.fsmonitor=false",
        "-c", "core.longpaths=true", "-c", "core.symlinks=true", "-c", "core.quotepath=false"];
    private readonly LocationInfo _location;
    private readonly bool _git;
    private readonly string _executable;
    private sealed record Result(int? ExitCode, string Text, bool Truncated);
    private sealed record Item(string File, string Code, FileDiffStatus Status);
    private sealed record Stat(int Additions, int Deletions);
    private sealed record BranchRef(string Name, string Ref);

    private LocalVcs(LocationInfo location, bool git, string executable) { _location = location; _git = git; _executable = executable; }

    public static async Task<LocalVcs> OpenAsync(LocationInfo location, string executable = "git", CancellationToken ct = default)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Explicit workspace VCS placement is not implemented.");
        var sources = await ConfigLoader.LoadSnapshotAsync(location.Directory, ct);
        foreach (var source in sources.Sources)
        {
            if (source is ConfigSource.Document document)
                foreach (var key in new[] { "plugin", "plugins" })
                    if (document.Info[key] is { } value && value is not JsonArray { Count: 0 } && value is not JsonObject { Count: 0 })
                        throw new NotSupportedException("Configured VCS/plugin providers require the native plugin runtime.");
            if (source is ConfigSource.Discovery { Entry: ConfigDirectory directory })
                foreach (var name in new[] { "plugin", "plugins" })
                {
                    var path = Path.Combine(directory.Path, name);
                    if (Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        throw new NotSupportedException("Discovered VCS/plugin providers require the native plugin runtime.");
                }
        }
        var git = Exists(Path.Combine(location.Project.Directory, ".git"));
        if (!git && Exists(Path.Combine(location.Project.Directory, ".hg")))
            throw new NotSupportedException("The native Mercurial VCS provider is not implemented.");
        return new(location, git, executable);
    }

    public async Task<VcsInfo> InfoAsync(CancellationToken ct = default)
    {
        if (!_git) return new(new VcsBranch());
        var current = BranchAsync(ct);
        var root = DefaultBranchAsync(ct);
        await Task.WhenAll(current, root);
        return new(new VcsBranch(await current, (await root)?.Name));
    }

    public async Task<VcsBase?> BaseAsync(CancellationToken ct = default)
    {
        if (!_git || !await HasHeadAsync(ct)) return null;
        var current = await BranchAsync(ct);
        if (current is null) throw new VcsUnavailableException("Choose a review base");
        var result = await RunAsync(_location.Directory, ["reflog", "show", "--max-count=256", "--format=%H%x00%gs", "refs/heads/" + current], ct);
        var history = Lines(result.Text).Select(line => Regex.Match(line, "^([a-f0-9]+)\\0(.+)$"))
            .Where(match => match.Success).Select(match => (Commit: match.Groups[1].Value, Message: match.Groups[2].Value)).ToArray();
        var creation = history.Any(entry => entry.Message.StartsWith("Branch: renamed ", StringComparison.Ordinal)) ? default
            : history.FirstOrDefault(entry => entry.Message.StartsWith("branch: Created from ", StringComparison.Ordinal));
        if (creation.Message is { } message)
        {
            var candidate = await NamedRefAsync(message["branch: Created from ".Length..], ct);
            if (candidate is not null && candidate.Name != current && await AncestorAsync(creation.Commit, "HEAD", ct)
                && await AncestorAsync(creation.Commit, candidate.Ref, ct)) return new(candidate.Name, candidate.Ref, "reflog");
        }
        var root = await DefaultBranchAsync(ct);
        if (root is null || current != root.Name) throw new VcsUnavailableException("Choose a review base");
        var named = await NamedRefAsync(root.Ref, ct);
        if (named is null) throw new VcsUnavailableException("The default review base is unavailable");
        return new(root.Name, named.Ref, "default");
    }

    private async Task<BranchRef?> NamedRefAsync(string input, CancellationToken ct)
    {
        if (input == "HEAD" || input.EndsWith("/HEAD", StringComparison.Ordinal) || Regex.IsMatch(input, @"[~^:@{}\s]")) return null;
        var result = await RunAsync(_location.Directory, ["rev-parse", "--symbolic-full-name", "--verify", "--end-of-options", input], ct);
        var reference = result.Text.Trim();
        if (result.ExitCode != 0 || !Regex.IsMatch(reference, "^refs/(heads|remotes)/.+") || reference.EndsWith("/HEAD", StringComparison.Ordinal)) return null;
        var commit = await RunAsync(_location.Directory, ["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], ct);
        return commit.ExitCode != 0 || commit.Text.Trim().Length == 0 ? null
            : new(Regex.Replace(Regex.Replace(reference, "^refs/heads/", ""), "^refs/remotes/[^/]+/", ""), reference);
    }

    private async Task<bool> AncestorAsync(string commit, string reference, CancellationToken ct) =>
        Regex.IsMatch(commit, "^[a-f0-9]{40,64}$") && (await RunAsync(_location.Directory, ["merge-base", "--is-ancestor", commit, reference], ct)).ExitCode == 0;

    public async Task<IReadOnlyList<string>> BranchesAsync(string? search = null, int limit = 50, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (!_git) return [];
        var escaped = Regex.Replace(search?.Trim() ?? "", @"[*?\[\]\\]", match => "\\" + match.Value);
        var result = await RunAsync(_location.Directory, ["for-each-ref", "--ignore-case", "--sort=refname", "--sort=-committerdate",
            "--format=%(refname:short)", "--count=" + Math.Min(limit, 100).ToString(CultureInfo.InvariantCulture),
            .. escaped.Length == 0 ? new[] { "refs/heads", "refs/remotes" } : ["refs/heads/*" + escaped + "*", "refs/remotes/*" + escaped + "*"]], ct);
        Success(result, "Unable to list Git branches");
        return Lines(result.Text).Where(branch => !branch.EndsWith("/HEAD", StringComparison.Ordinal)).ToArray();
    }

    public async Task<IReadOnlyList<VcsFileStatus>> StatusAsync(CancellationToken ct = default)
    {
        if (!_git) return [];
        var head = await HasHeadAsync(ct);
        var names = await StatusNamesAsync(ct);
        var stats = head ? await StatsAsync("HEAD", null, ct) : new Dictionary<string, Stat>(StringComparer.Ordinal);
        var output = new List<VcsFileStatus>();
        foreach (var item in names.OrderBy(item => item.File, StringComparer.CurrentCulture))
        {
            var stat = stats.GetValueOrDefault(item.File) ?? (item.Status == FileDiffStatus.Added ? await UntrackedStatAsync(item.File, ct) : null);
            output.Add(new(item.File, stat?.Additions ?? 0, stat?.Deletions ?? 0, item.Status switch
            { FileDiffStatus.Added => VcsFileChangeStatus.Added, FileDiffStatus.Deleted => VcsFileChangeStatus.Deleted, _ => VcsFileChangeStatus.Modified }));
        }
        return output;
    }

    public Task<IReadOnlyList<FileDiffInfo>> DiffAsync(VcsDiffMode mode, string? baseRef = null, int context = PatchContext, CancellationToken ct = default) =>
        DiffAsync(VcsDiffModeJsonConverter.Encode(mode), baseRef, context, ct);

    public async Task<IReadOnlyList<FileDiffInfo>> DiffAsync(string mode, string? baseRef = null, int context = PatchContext, CancellationToken ct = default)
    {
        if (mode is not ("working" or "branch" or "committed")) throw new ArgumentException("VCS mode must be working, branch, or committed.", nameof(mode));
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        if (!_git) return [];
        var head = await HasHeadAsync(ct);
        if (!head && mode == "committed") return [];
        var reference = head ? "HEAD" : null;
        var target = mode == "committed" ? "HEAD" : null;
        if (head && mode != "working")
        {
            var baseline = baseRef ?? (await DefaultBranchAsync(ct))?.Ref;
            reference = baseline is null ? null : await MergeBaseAsync(baseline, ct);
            if (reference is null) throw new VcsUnavailableException(baseline is null ? "No review base available" : $"No merge base available for {baseline}");
        }
        var listed = reference is null ? await StatusNamesAsync(ct) : await DiffNamesAsync(reference, target, ct);
        var stats = reference is null ? new Dictionary<string, Stat>(StringComparer.Ordinal) : await StatsAsync(reference, target, ct);
        var all = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in listed) all.TryAdd(item.File, item);
        if (reference is not null && target is null)
            foreach (var item in (await StatusNamesAsync(ct)).Where(item => item.Code == "??")) all.TryAdd(item.File, item);
        var batch = reference is null || listed.Count == 0 ? null : await PatchAsync(_location.Directory, reference, ".", target, context, ct);
        var patches = batch is null ? new Dictionary<string, string>(StringComparer.Ordinal)
            : GitPatch.Chunks(batch.Text, batch.Truncated, index => index < listed.Count ? listed[index].File : null);
        var output = new List<FileDiffInfo>();
        var total = 0;
        var capped = false;
        foreach (var item in all.Values.OrderBy(item => item.File, StringComparer.CurrentCulture))
        {
            var stat = stats.GetValueOrDefault(item.File) ?? (target is null && item.Status == FileDiffStatus.Added ? await UntrackedStatAsync(item.File, ct) : null);
            var patch = capped ? GitPatch.Empty(item.File) : patches.GetValueOrDefault(item.File);
            if (patch is null && item.Code != "??" && batch?.Truncated == true) patch = GitPatch.Empty(item.File);
            if (patch is null)
            {
                var native = item.Code == "??" || reference is null
                    ? await RunAsync(_location.Project.Directory, ["diff", "--no-index", "--patch", "--no-ext-diff", "--no-renames",
                        "--unified=" + context.ToString(CultureInfo.InvariantCulture), "--", "/dev/null", item.File], ct, PatchBytes)
                    : await PatchAsync(_location.Project.Directory, reference, item.File, target, context, ct);
                if (native.ExitCode is not (0 or 1)) throw new VcsUnavailableException("Unable to produce Git patch");
                patch = native.Truncated || native.Text.Length == 0 ? GitPatch.Empty(item.File) : native.Text;
            }
            if (!capped)
            {
                var bytes = Encoding.UTF8.GetByteCount(patch);
                if (total + bytes > PatchBytes) { capped = true; patch = GitPatch.Empty(item.File); }
                else { total += bytes; capped = total >= PatchBytes; }
            }
            output.Add(new(item.File, patch, stat?.Additions ?? 0, stat?.Deletions ?? 0, item.Status));
        }
        return output;
    }

    private async Task<string?> BranchAsync(CancellationToken ct)
    {
        var result = await RunAsync(_location.Directory, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct);
        return result.ExitCode == 0 ? Nonempty(result.Text.Trim()) : null;
    }

    private async Task<BranchRef?> DefaultBranchAsync(CancellationToken ct)
    {
        var remotes = await RunAsync(_location.Directory, ["remote"], ct);
        Success(remotes, "Unable to list Git remotes");
        var names = Lines(remotes.Text);
        var remote = names.Contains("origin") ? "origin" : names.Length == 1 ? names[0] : names.Contains("upstream") ? "upstream" : names.FirstOrDefault();
        if (remote is not null)
        {
            var result = await RunAsync(_location.Directory, ["symbolic-ref", "refs/remotes/" + remote + "/HEAD"], ct);
            var full = result.Text.Trim();
            if (result.ExitCode == 0 && full.StartsWith("refs/remotes/" + remote + "/", StringComparison.Ordinal))
                return new(full[("refs/remotes/" + remote + "/").Length..], full["refs/remotes/".Length..]);
        }
        var branches = await RunAsync(_location.Directory, ["for-each-ref", "--format=%(refname:short)", "refs/heads"], ct);
        Success(branches, "Unable to list Git branches");
        var local = Lines(branches.Text);
        var configured = await RunAsync(_location.Directory, ["config", "init.defaultBranch"], ct);
        var selected = Nonempty(configured.Text.Trim());
        var name = selected is not null && local.Contains(selected) ? selected : local.Contains("main") ? "main" : local.Contains("master") ? "master" : null;
        return name is null ? null : new(name, name);
    }

    private async Task<bool> HasHeadAsync(CancellationToken ct) =>
        (await RunAsync(_location.Directory, ["rev-parse", "--verify", "HEAD"], ct)).ExitCode == 0;

    private async Task<string?> MergeBaseAsync(string baseline, CancellationToken ct)
    {
        var resolved = await RunAsync(_location.Directory, ["rev-parse", "--verify", "--end-of-options", baseline + "^{commit}"], ct);
        if (resolved.ExitCode != 0) return null;
        var merge = await RunAsync(_location.Directory, ["merge-base", resolved.Text.Trim(), "HEAD"], ct);
        return merge.ExitCode == 0 ? Nonempty(merge.Text.Trim()) : null;
    }

    private async Task<IReadOnlyList<Item>> StatusNamesAsync(CancellationToken ct)
    {
        var result = await RunAsync(_location.Directory, ["status", "--porcelain=v1", "--untracked-files=all", "--no-renames", "-z", "--", "."], ct);
        Success(result, "Unable to list Git working-copy changes");
        return Nuls(result.Text).Where(row => row.Length > 3).Select(row => new Item(row[3..], row[..2], Kind(row[..2]))).ToArray();
    }

    private async Task<IReadOnlyList<Item>> DiffNamesAsync(string reference, string? target, CancellationToken ct)
    {
        var result = await RunAsync(_location.Directory, ["diff", "--no-ext-diff", "--no-renames", "--name-status", "-z", reference,
            .. target is null ? Array.Empty<string>() : [target], "--", "."], ct);
        Success(result, "Unable to list Git changes");
        var tokens = Nuls(result.Text);
        return tokens.Select((code, index) => index % 2 == 0 && index + 1 < tokens.Length ? new Item(tokens[index + 1], code, Kind(code)) : null)
            .OfType<Item>().ToArray();
    }

    private async Task<Dictionary<string, Stat>> StatsAsync(string reference, string? target, CancellationToken ct)
    {
        var result = await RunAsync(_location.Directory, ["diff", "--no-ext-diff", "--no-renames", "--numstat", "-z", reference,
            .. target is null ? Array.Empty<string>() : [target], "--", "."], ct);
        Success(result, "Unable to read Git change statistics");
        var stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        foreach (var row in Nuls(result.Text))
        {
            var values = row.Split('\t', 3);
            if (values.Length == 3 && values[2].Length > 0) stats[values[2]] = new(Count(values[0]), Count(values[1]));
        }
        return stats;
    }

    private async Task<Stat?> UntrackedStatAsync(string file, CancellationToken ct)
    {
        var result = await RunAsync(_location.Project.Directory, ["diff", "--no-index", "--numstat", "--", "/dev/null", file], ct, 4096);
        if (result.Truncated) return null;
        var values = result.Text.Split('\t');
        return values.Length < 2 ? null : new(Count(values[0]), Count(values[1]));
    }

    private Task<Result> PatchAsync(string directory, string reference, string file, string? target, int context, CancellationToken ct) =>
        RunAsync(directory, ["diff", "--patch", "--no-ext-diff", "--no-renames", "--unified=" + context.ToString(CultureInfo.InvariantCulture), reference,
            .. target is null ? Array.Empty<string>() : [target], "--", file], ct, PatchBytes);

    private async Task<Result> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken ct, int? budget = null)
    {
        ct.ThrowIfCancellationRequested();
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputHandle = input,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, InheritedHandles = []
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        foreach (var argument in Configuration.Concat(arguments)) start.ArgumentList.Add(argument);
        try
        {
            // Inherit the caller's environment, as extendEnv:true does; do not add
            // shell-tool terminal markers or mutate Git's index/config environment.
            var result = await Process.RunAndCaptureTextAsync(start, ct);
            ct.ThrowIfCancellationRequested();
            if (result.ExitStatus.Canceled) throw new OperationCanceledException(ct);
            if (result.ExitStatus.Signal is not null) throw new VcsUnavailableException("Git terminated before completing the requested read.");
            var bytes = Encoding.UTF8.GetBytes(result.StandardOutput);
            var truncated = budget is { } maximum && (bytes.Length > maximum || Encoding.UTF8.GetByteCount(result.StandardError) > maximum);
            return new(result.ExitStatus.ExitCode, budget is { } limit && bytes.Length > limit ? Encoding.UTF8.GetString(bytes, 0, limit) : result.StandardOutput, truncated);
        }
        catch (Win32Exception error) { throw new VcsUnavailableException("The Git executable is unavailable for this Location.", error); }
    }

    private static void Success(Result result, string message) { if (result.ExitCode != 0) throw new VcsUnavailableException(message); }
    private static string[] Nuls(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static string[] Lines(string value) => value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
    private static string? Nonempty(string value) => value.Length == 0 ? null : value;
    private static FileDiffStatus Kind(string code) => code == "??" ? FileDiffStatus.Added : code.Contains('U') ? FileDiffStatus.Modified
        : code.Contains('A') && !code.Contains('D') ? FileDiffStatus.Added : code.Contains('D') && !code.Contains('A') ? FileDiffStatus.Deleted : FileDiffStatus.Modified;
    private static int Count(string value)
    {
        if (value is "-" or "") return 0;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed is >= 0 and <= int.MaxValue ? (int)parsed : throw new NotSupportedException("Git change counts exceed the native schema range.");
        return 0;
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
