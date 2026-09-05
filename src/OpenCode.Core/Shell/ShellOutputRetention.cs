namespace OpenCode.Core.Shell;

using System.Text.RegularExpressions;

/// <summary>Seven-day mtime retention for explicitly owned shell directories; never recursive and never follows links.</summary>
public static class ShellOutputRetention
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static async Task CleanupAsync(IEnumerable<string> directories, IReadOnlyCollection<string> protectedFiles, CancellationToken ct = default, TimeProvider? clock = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var owned = directories.Select(directory =>
        {
            if (!Path.IsPathFullyQualified(directory) || Path.TrimEndingDirectorySeparator(directory) == Path.GetPathRoot(directory))
                throw new ArgumentException("Retention requires explicit private output directories, not relative paths or volume roots.", nameof(directories));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }).Distinct(comparer).ToArray();
        var active = protectedFiles.Select(Path.GetFullPath).ToHashSet(comparer);
        var cutoff = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime - Retention;
        await Task.Yield();
        foreach (var directory in owned)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var folder = new DirectoryInfo(directory);
                if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != (FileAttributes)0) continue;
                foreach (var file in folder.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Regex.IsMatch(file.Name, @"^sh_[0-9a-f]{12}.*\.out$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
                        || active.Contains(Path.GetFullPath(file.FullName))) continue;
                    try
                    {
                        file.Refresh();
                        if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != (FileAttributes)0
                            || file.LastWriteTimeUtc >= cutoff) continue;
                        file.Delete();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
