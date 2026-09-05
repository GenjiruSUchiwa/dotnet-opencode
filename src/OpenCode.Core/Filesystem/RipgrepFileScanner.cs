namespace OpenCode.Core.Filesystem;

using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Clean domain boundary over the existing public process adapter. No regex/glob reimplementation.</summary>
public sealed class RipgrepFileScanner(RipgrepProcess process)
{
    public async Task<IReadOnlyList<FileSystemEntry>> ScanAsync(LocationInfo location, CancellationToken ct)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Workspace search requires an environment-owned ripgrep spawner.");
        var home = Path.GetFullPath(location.Directory) == Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var exclusions = !home ? Array.Empty<string>() : OperatingSystem.IsWindows()
            ? ["AppData", "Downloads", "Desktop", "Documents", "Pictures", "Music", "Videos", "OneDrive"]
            : OperatingSystem.IsMacOS()
                ? ["Music", "Pictures", "Movies", "Downloads", "Desktop", "Documents", "Public", "Applications", "Library"] : [];
        // GlobAsync adds --glob=<pattern>. Only negative globs are valid here: a positive
        // --glob=* would override .gitignore/.ignore and include hidden files, unlike find.
        var pattern = exclusions.Length == 0 ? "!**/.git/**" : "!{" + string.Join(',', exclusions.Select(name => name + "/**")) + "}";
        var vcs = Path.Exists(Path.Combine(location.Project.Directory, ".git")) || Path.Exists(Path.Combine(location.Project.Directory, ".hg"));
        var limit = vcs && !home ? int.MaxValue : 100_000;
        var files = await process.GlobAsync(location.Directory, pattern, limit, ct);
        var entries = new Dictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        var directories = new Dictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            entries.TryAdd(file.Path, new(file.Path, FileSystemEntryType.File));
            var parts = file.Path.Split('/');
            for (var count = 1; count < parts.Length; count++)
            {
                var directory = string.Join('/', parts, 0, count) + Path.DirectorySeparatorChar;
                directories.TryAdd(directory, new(directory, FileSystemEntryType.Directory));
            }
        }
        // Source index order is files first, then inferred directories; empty directories are absent.
        return [.. entries.Values, .. directories.Values];
    }
}
