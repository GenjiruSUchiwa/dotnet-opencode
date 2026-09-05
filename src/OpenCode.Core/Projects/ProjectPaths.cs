namespace OpenCode.Core.Projects;

using System.Text.RegularExpressions;

/// <summary>database/path.ts absolute-column encoding, including portable stored Windows paths.</summary>
internal static class ProjectPaths
{
    internal static string Storage(string input)
    {
        var path = OperatingSystem.IsWindows() ? input.Replace('\\', '/') : input;
        if (!path.StartsWith('/') && !Regex.IsMatch(path, "^[A-Za-z]:/")) throw new InvalidDataException($"Path is not absolute: {input}");
        return path;
    }

    internal static string Platform(string input)
    {
        var path = Storage(input);
        return OperatingSystem.IsWindows() && (Regex.IsMatch(path, "^[A-Za-z]:/") || path.StartsWith("//", StringComparison.Ordinal))
            ? path.Replace('/', '\\') : path;
    }
}
