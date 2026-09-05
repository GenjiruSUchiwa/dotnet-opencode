namespace OpenCode.Cli.Commands.Run;

internal static partial class RunMimeRegistry
{
    public static string Lookup(string path)
    {
        // mime-types uses extname('x.' + path), lowercased. RunFiles supplies an
        // absolute path; implement Node's last-component/dotfile extension rules.
        var value = "x." + path;
        var slash = OperatingSystem.IsWindows() ? Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\')) : value.LastIndexOf('/');
        var name = value[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot <= 0 || name == ".." ? "" : name[(dot + 1)..].ToLowerInvariant();
        return Types.GetValueOrDefault(extension, "application/octet-stream");
    }
}
