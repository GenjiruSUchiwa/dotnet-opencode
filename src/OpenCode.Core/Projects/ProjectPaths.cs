namespace OpenCode.Core.Projects;

/// <summary>database/path.ts absolute-column encoding, including portable stored Windows paths.</summary>
internal static class ProjectPaths
{
    internal static string Storage(string input)
    {
        var path = OperatingSystem.IsWindows() ? input.Replace('\\', '/') : input;
        if (!path.StartsWith('/') && !IsWindowsStorage(path)) throw new InvalidDataException($"Path is not absolute: {input}");
        return path;
    }

    internal static string Platform(string input)
    {
        var path = Storage(input);
        return OperatingSystem.IsWindows() && IsWindowsStorage(path)
            ? path.Replace('/', '\\') : path;
    }

    internal static bool IsWindowsStorage(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
        || path.StartsWith("//", StringComparison.Ordinal);
}
