namespace OpenCode.Core.Tools;

internal static class LocalToolPath
{
    // FSUtil.resolve follows existing components, retaining the lexical absolute path on missing targets.
    public static string Resolve(string path)
    {
        var absolute = Path.GetFullPath(path);
        try
        {
            var current = Path.GetPathRoot(absolute)!;
            foreach (var component in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                var info = (attributes & FileAttributes.Directory) != 0 ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException($"Unable to resolve link: {current}");
            }
            return current;
        }
        catch (FileNotFoundException) { return absolute; }
        catch (DirectoryNotFoundException) { return absolute; }
    }
}
