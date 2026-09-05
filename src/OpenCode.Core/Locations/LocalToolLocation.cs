namespace OpenCode.Core.Locations;

using System.Text.RegularExpressions;
using OpenCode.Core.Tools;

/// <summary>Local implementation of LocationMutation. Authorization is lexical, not a symlink sandbox.</summary>
public sealed class LocalToolLocation : IToolLocation
{
    private readonly string _worktree;
    private readonly string _home;
    private readonly string[] _markers;
    public string Directory { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Preserve the existing compound Location/path and marker error text exposed by tools.")]
    public LocalToolLocation(string directory, string projectWorktree, string home, IEnumerable<string>? markers = null)
    {
        if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(projectWorktree) || !Path.IsPathFullyQualified(home))
            throw new ArgumentException("Location, project worktree and home must be absolute paths.");
        Directory = Path.GetFullPath(directory);
        _worktree = Path.GetFullPath(projectWorktree);
        _home = Path.GetFullPath(home);
        _markers = (markers ?? [".git", ".hg"]).Distinct(StringComparer.Ordinal).ToArray();
        if (_markers.Any(marker => string.IsNullOrEmpty(marker) || marker is "." or ".." || marker.Contains('/') || marker.Contains('\\')))
            throw new ArgumentException("Project markers must be single entry names.");
    }

    public string ResolvePath(string input, string? directory = null)
    {
        var normalized = WindowsPath(input);
        var expanded = normalized == "~" ? _home
            : normalized.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal))
                ? Path.Combine(_home, normalized[2..]) : normalized;
        return Path.GetFullPath(expanded.Length == 0 ? "." : expanded, directory ?? Directory);
    }

    public Task<ToolPath> ResolveAsync(string path, ToolPathKind? kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var absolute = ResolvePath(path);
        if (Contains(Directory, absolute) || (!PathEquals(_worktree, Path.GetPathRoot(_worktree)!) && Contains(_worktree, absolute)))
            return Task.FromResult(new ToolPath(absolute, Slash(Path.GetRelativePath(Directory, absolute))));
        var isDirectory = kind == ToolPathKind.Directory || (kind is null && IsDirectoryFollowing(absolute));
        var external = isDirectory ? absolute : Path.GetDirectoryName(absolute)!;
        var root = FindProjectRoot(external, ct) ?? external;
        return Task.FromResult(new ToolPath(absolute, Slash(absolute),
            new ExternalToolDirectory(external, Slash(Path.Combine(external, "*")), Slash(Path.Combine(root, "*")))));
    }

    public static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static string WindowsPath(string input)
    {
        if (!OperatingSystem.IsWindows()) return input;
        foreach (var pattern in new[] { @"^/([a-zA-Z]):(?:[\\/]|$)", @"^/([a-zA-Z])(?:/|$)", @"^/cygdrive/([a-zA-Z])(?:/|$)", @"^/mnt/([a-zA-Z])(?:/|$)" })
            input = Regex.Replace(input, pattern, match => match.Groups[1].Value.ToUpperInvariant() + ":/", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return input;
    }

    private static bool IsDirectoryFollowing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.None) return (attributes & FileAttributes.Directory) != FileAttributes.None;
            var target = ((attributes & FileAttributes.Directory) != FileAttributes.None ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).ResolveLinkTarget(true);
            return target is not null && (target.Attributes & FileAttributes.Directory) != FileAttributes.None;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private string? FindProjectRoot(string directory, CancellationToken ct)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            ct.ThrowIfCancellationRequested();
            // Project.root deliberately tolerates failed marker discovery; no VCS commands are run.
            if (_markers.Any(marker => Path.Exists(Path.Combine(current, marker)))) return current;
        }
        return null;
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Slash(string path) => path.Replace('\\', '/');
}
