namespace OpenCode.Core.Worktrees;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OpenCode.Schema;

public sealed class WorktreeException(string message, bool? forceRequired = null, Exception? inner = null) : IOException(message, inner)
{ public bool? ForceRequired { get; } = forceRequired; }
public sealed record WorktreeListEntry(string Directory, bool Root);
public interface IWorktreeStrategy
{
    string Id { get; }
    Task<WorktreeInfo> CreateAsync(string source, string directory, string? branch, CancellationToken ct);
    Task RemoveAsync(string directory, bool force, CancellationToken ct);
    Task<IReadOnlyList<WorktreeListEntry>> ListAsync(string directory, CancellationToken ct);
}

public sealed class GitWorktreeStrategy(string executable = "git") : IWorktreeStrategy
{
    internal sealed record Repository(string Worktree, string GitDirectory, string CommonDirectory);
    public string Id => "git";

    public async Task<WorktreeInfo> CreateAsync(string source, string directory, string? branch, CancellationToken ct)
    {
        var repository = await DiscoverAsync(source, ct).ConfigureAwait(true);
        await RunAsync(repository.Worktree, ["worktree", "add", "--detach", "--", directory, branch ?? "HEAD"], ct).ConfigureAwait(true);
        await DiscoverAsync(directory, ct).ConfigureAwait(true);
        return new(WorktreePaths.Canonical(directory));
    }

    public async Task RemoveAsync(string directory, bool force, CancellationToken ct)
    {
        var repository = await DiscoverAsync(directory, ct).ConfigureAwait(true);
        await RunAsync(repository.CommonDirectory, ["worktree", "remove", .. force ? new[] { "--force" } : [], directory], ct, removal: true).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<WorktreeListEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var repository = await DiscoverAsync(directory, ct).ConfigureAwait(true);
        var output = await RunAsync(repository.Worktree, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(true);
        var result = new List<WorktreeListEntry>();
        foreach (var row in output.Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select((line, index) => (line, index)))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(row.line["worktree ".Length..].Trim(), repository.Worktree);
            try { result.Add(new(WorktreePaths.Canonical(path), row.index == 0)); }
            catch (WorktreeException) { }
        }
        return result;
    }

    internal async Task<Repository> DiscoverAsync(string directory, CancellationToken ct)
    {
        var root = new DirectoryInfo(WorktreePaths.Canonical(directory));
        while (root is not null && !WorktreePaths.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        if (root is null) throw new WorktreeException($"Worktree directory unavailable: {directory}");
        var output = await RunAsync(root.FullName, ["rev-parse", "--git-dir", "--git-common-dir", "--show-toplevel"], ct).ConfigureAwait(true);
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length < 3 || lines.Take(3).Any(string.IsNullOrEmpty)) throw new WorktreeException($"Worktree directory unavailable: {directory}");
        return new(WorktreePaths.Canonical(Path.GetFullPath(lines[2], root.FullName)),
            WorktreePaths.Canonical(Path.GetFullPath(lines[0], root.FullName)), WorktreePaths.Canonical(Path.GetFullPath(lines[1], root.FullName)));
    }

    private async Task<string> RunAsync(string directory, IReadOnlyList<string> args, CancellationToken ct, bool removal = false)
    {
        ct.ThrowIfCancellationRequested();
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, InheritedHandles = [],
            StandardInputHandle = input, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        foreach (var argument in args) start.ArgumentList.Add(argument);
        try
        {
            var result = await Process.RunAndCaptureTextAsync(start, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            if (result.ExitStatus.Canceled) throw new OperationCanceledException(ct);
            if (result.ExitStatus.ExitCode == 0 && result.ExitStatus.Signal is null) return result.StandardOutput;
            var message = result.StandardError.Trim();
            if (message.Length == 0) message = result.StandardOutput.Trim();
            if (message.Length == 0) message = "Git failed";
            throw new WorktreeException(message, removal && Regex.IsMatch(message, "contains modified or untracked files|is dirty", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));
        }
        catch (Win32Exception error) { throw new WorktreeException("The Git executable is unavailable for worktree operations.", inner: error); }
        catch (ArgumentException error) { throw new WorktreeException(error.Message, inner: error); }
    }
}
