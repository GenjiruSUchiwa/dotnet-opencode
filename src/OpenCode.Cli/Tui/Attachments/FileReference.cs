namespace OpenCode.Cli.Tui.Attachments;

using System.Globalization;
using System.Text.RegularExpressions;
using OpenCode.Schema;

public enum AttachmentKind { File, Directory, Agent, Skill }
public sealed record AttachmentTarget(AttachmentKind Kind, string Key);
public sealed record ReferenceOption(string Label, AttachmentKind Kind, string Key, string? Description = null)
{
    public string Identity => Kind + ":" + Key;
}
public sealed record ReferenceQuery(int Start, int Cursor, string Search, string Path, long? StartLine, long? EndLine)
{
    public static ReferenceQuery? Parse(string text, int cursor)
    {
        if (cursor <= 0 || cursor > text.Length) return null;
        var start = text.LastIndexOf('@', cursor - 1);
        if (start < 0 || start > 0 && !char.IsWhiteSpace(text[start - 1])) return null;
        var query = text[(start + 1)..cursor];
        if (query.Any(char.IsWhiteSpace)) return null;
        var match = Regex.Match(query, @"#(?<first>\d+)(?:-(?<last>\d*))?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first))
            return new(start, cursor, query, query, null, null);
        var last = long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end > first ? end : (long?)null;
        return new(start, cursor, query, query[..match.Index], first, last);
    }
}

public static class FileReference
{
    public static ReferenceOption Create(LocationInfo location, FileSystemEntry entry, ReferenceQuery query)
    {
        var ranged = entry.Type != FileSystemEntryType.Directory && query.StartLine is not null;
        var label = entry.Path + (ranged ? $"#{query.StartLine}" + (query.EndLine is { } end ? $"-{end}" : "") : "");
        var uri = FileUri(location.Directory, entry.Path);
        if (ranged) uri += "?start=" + query.StartLine!.Value.ToString(CultureInfo.InvariantCulture)
            + (query.EndLine is { } last ? "&end=" + last.ToString(CultureInfo.InvariantCulture) : "");
        return new(label, entry.Type == FileSystemEntryType.Directory ? AttachmentKind.Directory : AttachmentKind.File, uri);
    }

    // Location belongs to the server. Path.GetFullPath/pathToFileURL on the client
    // would reinterpret a remote POSIX path as a Windows path (or vice versa).
    public static string FileUri(string directory, string relative)
    {
        var path = directory.Replace('\\', '/').TrimEnd('/') + "/" + relative.Replace('\\', '/');
        var unc = path.StartsWith("//", StringComparison.Ordinal);
        var drivePath = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        var pieces = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (pieces.Count > (unc ? 2 : drivePath ? 1 : 0)) pieces.RemoveAt(pieces.Count - 1); continue; }
            pieces.Add(part);
        }
        if (unc) return "file://" + pieces[0] + "/" + string.Join('/', pieces.Skip(1).Select(Uri.EscapeDataString));
        var drive = pieces.Count > 0 && pieces[0].Length == 2 && char.IsAsciiLetter(pieces[0][0]) && pieces[0][1] == ':';
        if (!drive && !path.StartsWith('/')) throw new ArgumentException("The server did not return an absolute Location directory.", nameof(directory));
        return "file:///" + string.Join('/', pieces.Select((part, index) => drive && index == 0 ? part : Uri.EscapeDataString(part)));
    }
}
