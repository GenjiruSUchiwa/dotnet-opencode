namespace OpenCode.Core.Filesystem;

using System.Globalization;
using OpenCode.Schema;

/// <summary>Local read/list boundary from core/filesystem.ts. No tool permission prompts:
/// authenticated HTTP callers may access files only inside the resolved Location.</summary>
public sealed class LocalFileSystem(LocationInfo location)
{
    public IReadOnlyList<FileSystemEntry> List(string? path = null, CancellationToken ct = default)
    {
        var target = Resolve(path);
        if ((File.GetAttributes(target.Real) & FileAttributes.Directory) == FileAttributes.None)
            throw new IOException("Path is not a directory.");
        var entries = new List<FileSystemEntry>();
        // Source readdir(withFileTypes) includes hidden/ignored children, not symlinks.
        foreach (var item in new DirectoryInfo(target.Real).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false, ReturnSpecialDirectories = false
        }))
        {
            ct.ThrowIfCancellationRequested();
            if ((item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.None) continue;
            var directory = (item.Attributes & FileAttributes.Directory) != FileAttributes.None;
            entries.Add(new(Path.GetRelativePath(location.Directory, Path.Combine(target.Absolute, item.Name))
                + (directory ? Path.DirectorySeparatorChar.ToString() : ""),
                directory ? FileSystemEntryType.Directory : FileSystemEntryType.File));
        }
        // JS localeCompare uses linguistic sorting with lowercase before uppercase by default.
        entries.Sort((left, right) => left.Type != right.Type
            ? left.Type == FileSystemEntryType.Directory ? -1 : 1 : CompareNames(left.Path, right.Path));
        return entries;
    }

    public (FileStream Content, string Mime) Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var target = Resolve(path);
        if ((File.GetAttributes(target.Real) & (FileAttributes.Directory | FileAttributes.Device)) != FileAttributes.None)
            throw new IOException("Path is not a file.");
        var mime = FilesystemMime.Lookup(target.Real);
        // Stream a fixed-size buffer rather than allocating the entire file as upstream does.
        return (new FileStream(target.Real, new FileStreamOptions
        {
            Access = FileAccess.Read, Mode = FileMode.Open, Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 64 * 1024
        }), mime);
    }

    private (string Absolute, string Real) Resolve(string? path)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Workspace filesystem placement is not implemented.");
        // The current port has no Unix lstat file-kind adapter. Do not mistake FIFOs/devices
        // for regular files and block an HTTP read while opening one.
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Local HTTP read/list requires the Windows file-kind adapter; Unix special-file classification is not implemented.");
        var absolute = Path.GetFullPath(string.IsNullOrEmpty(path) ? "." : path, location.Directory);
        if (!Contains(location.Directory, absolute)) throw new UnauthorizedAccessException("Path escapes the location.");
        var real = RealPath(absolute);
        if (!Contains(RealPath(location.Directory), real)) throw new UnauthorizedAccessException("Path escapes the location.");
        return (absolute, real);
    }

    private static string RealPath(string path, int links = 0)
    {
        if (links > 63) throw new IOException("Too many symbolic links.");
        var absolute = Path.GetFullPath(path);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var component in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current); // Missing paths are errors, never empty successes.
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.None) continue;
            var info = (attributes & FileAttributes.Directory) != FileAttributes.None ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            current = RealPath(info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unable to resolve symbolic link."), links + 1);
        }
        return current;
    }

    private static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static int CompareNames(string left, string right)
    {
        var comparison = CultureInfo.CurrentCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase);
        if (comparison != 0) return comparison;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            if (left[index] != right[index] && char.ToUpperInvariant(left[index]) == char.ToUpperInvariant(right[index]))
                return char.IsLower(left[index]) ? -1 : 1;
        return CultureInfo.CurrentCulture.CompareInfo.Compare(left, right, CompareOptions.None);
    }
}
