namespace OpenCode.Core.Tools.Builtins;
using Transport;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record ToolReadFile(string Uri, string Name, string Content, string Encoding, string Mime)
{
    public string Type => "file";
}
public sealed record ToolReadTextPage(string Content, string Mime, int Offset, bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Next = null)
{
    public string Type => "text-page";
}
public sealed record ToolReadEntry(string Path, string Type);
public sealed record ToolReadListPage(IReadOnlyList<ToolReadEntry> Entries, bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Next = null)
{
    public string Type => "list-page";
}

/// <summary>Local source-backed file/media and text/directory pages. Session instruction discovery remains separate.</summary>
public sealed class ReadTool(ToolFilePolicy? policy = null, ReadInstructionDiscovery? instructions = null)
{
    private const int MaximumTextBytes = 50 * 1024;
    private const int MaximumMediaBytes = 20 * 1024 * 1024;
    private const int MaximumDirectoryEntries = 100_000;
    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, BuiltinToolSchemas.Read, new ToolOptions(CodeMode: false));
    public string Name => "read";
    public string Description => "Read files or directories. Text lines have 1-based reference prefixes. Supports PNG, JPEG, GIF, WebP and PDF media up to 20 MiB. Use offset and limit for text and directory pages.";
    public JsonElement InputSchema => JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":0},
          "limit":{"type":"integer","minimum":0,"maximum":2000}},"required":["path"]}
        """);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var args = new ToolInput(input);
        var path = args.String("path");
        var offset = Math.Max(1, args.Integer("offset", 1, 0));
        var limit = args.Integer("limit", 2000, 0, 2000);
        if (limit == 0) limit = 2000;
        if (policy is null || instructions is null)
            throw new NotSupportedException("read requires Location, permission and the Session-owned read-instruction loader.");
        var target = await policy.ResolveAsync(path, null, context, ct).ConfigureAwait(true);
        await policy.AssertAsync(Name, [target.Resource], ["*"], context, null, ct).ConfigureAwait(true);
        object output;
        try
        {
            output = await ReadLocalAsync(target, offset, limit,
                input.TryGetProperty("offset", out _) || input.TryGetProperty("limit", out _), ct).ConfigureAwait(true);
        }
        catch (FileNotFoundException error) { throw new ToolExecutionException($"File not found: {path}", error); }
        catch (DirectoryNotFoundException error) { throw new ToolExecutionException($"File not found: {path}", error); }
        catch (IOException error) { throw new ToolExecutionException($"Unable to read {path}: {error.Message}", error); }
        catch (UnauthorizedAccessException error) { throw new ToolExecutionException($"Unable to read {path}: {error.Message}", error); }

        await instructions.AfterReadAsync(context.SessionId, target, output is ToolReadListPage, ct).ConfigureAwait(true);

        if (output is ToolReadFile { Encoding: "base64" } media)
            return new ToolExecutionResult
            {
                Output = media,
                Content = [new ToolTextContent(media.Mime == "application/pdf" ? "PDF read successfully" : "Image read successfully"),
                    new ToolFileContent($"data:{media.Mime};base64,{media.Content}", media.Mime, path)],
                Metadata = new Dictionary<string, object> { ["truncated"] = false }
            };
        if (output is ToolReadListPage listing)
        {
            var content = listing.Entries.Count == 0 ? $"Read directory {path}, 0 entries"
                : $"Read directory {path}, entries {offset}-{offset + listing.Entries.Count - 1}";
            if (listing.Entries.Count > 0) content += "\n" + string.Join('\n', listing.Entries.Select(entry => entry.Path));
            if (listing.Next is { } next) content += $"\n[Output truncated. Continue reading with offset: {next}]";
            return new(content, listing, new Dictionary<string, object> { ["truncated"] = listing.Truncated });
        }
        var text = output is ToolReadTextPage page ? page.Content : ((ToolReadFile)output).Content;
        var start = output is ToolReadTextPage textPage ? textPage.Offset : 1;
        var lines = text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        var rendered = new StringBuilder(lines.Length == 0 ? $"Read file {path}, 0 lines" : $"Read file {path}, lines {start}-{(long)start + lines.Length - 1}");
        foreach (var line in lines.Select((value, index) => $"{(long)start + index}: {value}")) rendered.Append('\n').Append(line);
        if (output is ToolReadTextPage { Next: { } continuation }) rendered.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n[Output truncated. Continue reading with offset: {continuation}]");
        return new(rendered.ToString(), output, new Dictionary<string, object> { ["truncated"] = output is ToolReadTextPage { Truncated: true } });
    }

    private static async Task<object> ReadLocalAsync(ToolPath target, int offset, int limit, bool explicitPage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(target.Absolute))
        {
            var entries = new List<ToolReadEntry>();
            foreach (var entry in new DirectoryInfo(target.Absolute).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count == MaximumDirectoryEntries)
                    throw new ToolExecutionException($"Directory exceeds the local {MaximumDirectoryEntries} entry limit. Use glob with a narrower pattern.");
                var type = (entry.Attributes & FileAttributes.ReparsePoint) != (FileAttributes)0 ? "symlink"
                    : (entry.Attributes & FileAttributes.Directory) != (FileAttributes)0 ? "directory" : "file";
                if ((entry.Attributes & FileAttributes.Device) != (FileAttributes)0) continue;
                entries.Add(new(entry.Name + (type == "directory" ? Path.DirectorySeparatorChar.ToString() : ""), type));
            }
            var selected = entries.OrderBy(entry => entry.Type == "directory" ? 0 : 1)
                .ThenBy(entry => entry.Path, StringComparer.CurrentCulture).Skip(offset - 1).Take(limit).ToArray();
            var truncated = (long)offset - 1 + selected.Length < entries.Count;
            return new ToolReadListPage(selected, truncated, truncated ? (long)offset + selected.Length : null);
        }
        var stream = new FileStream(target.Absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var streamLifetime = stream.ConfigureAwait(false);
        var size = stream.Length;
        var first = new byte[256 * 1024];
        var count = await stream.ReadAtLeastAsync(first, first.Length, false, ct).ConfigureAwait(false);
        var media = MediaMime(first.AsSpan(0, count));
        if (media is not null)
        {
            if (size > MaximumMediaBytes) throw new ToolExecutionException($"Media exceeds {MaximumMediaBytes} byte ingestion limit: {target.Resource}");
            var bytes = await PipelineBytes.CollectAsync(stream, MaximumMediaBytes,
                () => new ToolExecutionException($"Media exceeds {MaximumMediaBytes} byte ingestion limit: {target.Resource}"), ct, first.AsMemory(0, count)).ConfigureAwait(false);
            return new ToolReadFile(new Uri(target.Absolute).AbsoluteUri, Path.GetFileName(target.Absolute), Convert.ToBase64String(bytes), "base64", media);
        }
        var mime = TextMime(target.Absolute);
        if (size <= MaximumTextBytes && count <= MaximumTextBytes && !explicitPage)
        {
            if (first.AsSpan(0, count).Contains((byte)0)) throw new ToolExecutionException($"Cannot read binary file: {target.Resource}");
            var text = Encoding.UTF8.GetString(first, 0, count);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            return new ToolReadFile(new Uri(target.Absolute).AbsoluteUri, Path.GetFileName(target.Absolute), text, "utf8", mime);
        }

        stream.Position = 0;
        var entriesText = new List<string>();
        var line = new StringBuilder(2001);
        long number = 1;
        long? nextLine = null;
        var bytesUsed = 0;
        var binary = false;
        var lineBinary = false;
        var hasLine = false;
        var firstCharacter = true;
        var done = false;
        await foreach (var chunk in PipelineText.ChunksAsync(stream, new UTF8Encoding(false), detectBom: false, cancellationToken: ct).ConfigureAwait(false))
        {
            if (done) break;
            for (var index = 0; index < chunk.Length; index++)
            {
                var character = chunk[index];
                if (firstCharacter) { firstCharacter = false; if (character == '\uFEFF') continue; }
                if (number >= offset && (entriesText.Count >= limit || bytesUsed >= MaximumTextBytes))
                { nextLine = number; done = true; break; }
                if (character == '\n')
                {
                    if (!CompleteLine()) { done = true; break; }
                    continue;
                }
                hasLine = true;
                lineBinary |= character == '\0';
                if (number >= offset && line.Length < 2002) line.Append(character);
            }
            if (done) break;
        }
        if (!done && hasLine) CompleteLine();
        if (binary) throw new ToolExecutionException($"Cannot read binary file: {target.Resource}");
        if (entriesText.Count == 0 && offset != 1) throw new ToolExecutionException($"Offset {offset} is out of range");
        return new ToolReadTextPage(string.Join('\n', entriesText), mime, offset, nextLine is not null, nextLine);

        bool CompleteLine()
        {
            if (number >= offset)
            {
                var text = line.ToString();
                if (text.EndsWith('\r')) text = text[..^1];
                if (text.Length > 2000)
                {
                    var end = char.IsHighSurrogate(text[1999]) ? 1999 : 2000;
                    text = text[..end] + "... (line truncated to 2000 chars)";
                }
                var length = Encoding.UTF8.GetByteCount(text) + (entriesText.Count > 0 ? 1 : 0);
                if (bytesUsed + length > MaximumTextBytes) { nextLine = number; return false; }
                entriesText.Add(text);
                bytesUsed += length;
            }
            binary |= lineBinary;
            lineBinary = false;
            line.Clear();
            hasLine = false;
            number++;
            return true;
        }
    }

    private static string? MediaMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8)) return "image/webp";
        return null;
    }

    private static string TextMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" => "text/plain", ".md" or ".markdown" => "text/markdown", ".json" => "application/json",
        ".html" or ".htm" => "text/html", ".css" => "text/css", ".js" or ".mjs" => "text/javascript",
        ".xml" => "application/xml", ".svg" => "image/svg+xml", ".csv" => "text/csv", _ => "application/octet-stream"
    };
}
