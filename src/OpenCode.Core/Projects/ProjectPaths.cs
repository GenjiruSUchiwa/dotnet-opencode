namespace OpenCode.Core.Projects;

/// <summary>database/path.ts absolute-column encoding, including portable stored Windows paths.</summary>
internal static class ProjectPaths
{
    internal static string Storage(string input)
    {
        var path = Relative(input);
        if (!path.StartsWith('/') && !IsWindowsStorage(path)) throw new InvalidDataException($"Path is not absolute: {input}");
        return path;
    }

    internal static string Platform(string input)
    {
        var path = Storage(input);
        return OperatingSystem.IsWindows() && IsWindowsStorage(path)
            ? path.Replace('/', '\\') : path;
    }

    // database/path.ts keeps empty legacy Session directories readable. New
    // nonempty directories still use the same absolute-column validation.
    internal static string DirectoryStorage(string input) => input.Length == 0 ? input : Storage(input);
    internal static string DirectoryPlatform(string input) => input.Length == 0 ? input : Platform(input);

    // A subpath is not an absolute directory. Preserve empty values and normalize
    // separators only on Windows, including when decoding a stored subpath.
    internal static string Relative(string input) => OperatingSystem.IsWindows() ? input.Replace('\\', '/') : input;

    internal static bool IsWindowsStorage(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
        || path.StartsWith("//", StringComparison.Ordinal);
}
