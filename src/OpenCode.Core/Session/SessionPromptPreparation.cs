namespace OpenCode.Core.Session;
using Transport;

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Schema;

public sealed class PromptAttachmentException(string uri, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Uri { get; } = uri;
}

public sealed class PromptSkillNotFoundException(SkillId skill) : InvalidOperationException($"Skill not found: {skill.Value}")
{
    public SkillId Skill { get; } = skill;
}

/// <summary>The host's real Image.normalize adapter. Absence follows source ResizerUnavailable fallback.</summary>
public delegate ValueTask<PromptFileAttachment> NormalizePromptImage(PromptFileAttachment file, CancellationToken ct);

/// <summary>Local SessionPrompt.prepare plus reconcile/admit ordering. No model selection, instruction epoch,
/// tool invocation, or advisory wake occurs here. The caller owns scheduling after durable admission.</summary>
public sealed class SessionPromptPreparation(SessionStore sessions, NormalizePromptImage? normalizeImage = null,
    Func<SessionInfo, CancellationToken, Task>? commitRevert = null)
{
    public const int MaxAttachmentBytes = 20 * 1024 * 1024;

    public Task<SessionInboxItem> AdmitAsync(SessionId sessionId, PromptInput input, MessageId? id = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer,
        CancellationToken ct = default) => SessionRunCoordinator.AdmitAsync(sessionId, async () =>
        {
            var messageId = id ?? MessageId.Create();
            var existing = await sessions.ReconcileInboxAsync(sessionId, messageId, "user", delivery, ct);
            if (existing is not null) return existing;
            var session = await sessions.GetSessionAsync(sessionId, ct) ?? throw new InvalidOperationException("Session not found.");
            var capturedMetadata = metadata?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            var prepared = await PrepareAsync(session.Location, input, ct);
            if (session.Revert is not null)
            {
                if (commitRevert is null) throw new NotSupportedException("The host must supply durable staged-revert commit after prompt preparation.");
                await commitRevert(session, ct);
            }
            return await sessions.AdmitInboxAsync(sessionId, messageId,
                new UserInboxPayload(prepared.Text, prepared.Files, prepared.Agents, prepared.Skills, capturedMetadata), delivery, ct);
        }, ct);

    public async Task<Prompt> PrepareAsync(LocationRef location, PromptInput input, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        input = new PromptInput(input.Text, input.Files?.ToArray(), input.Agents?.ToArray(), input.Skills?.ToArray());
        if (location.WorkspaceId is not null) throw new NotSupportedException("Prompt preparation requires local filesystem placement.");
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var producers = ProducerConfiguration.Read(location.Directory, home, Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory()),
            ConfigLoader.LoadDocument(directory: location.Directory));
        if (!producers.ReferencesAvailable) throw new IOException("Prompt plugin configuration is unavailable.");
        // Source flushes plugins and invokes the prompt hook before preparing attachments.
        // No configured/discovered plugin may be silently skipped in this local slice.
        producers.RequireNoPluginSources();
        var files = input.Files is null ? null : new PromptFileAttachment[input.Files.Count];
        if (files is not null)
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
                    files[index] = await MaterializeAsync(input.Files![index], token));
        var skills = new List<PromptSkillAttachment>();
        if (input.Skills is { Count: > 0 })
        {
            var catalog = await InstructionCatalog.ListSkillsAsync(location.Directory, ct);
            var prepared = new Dictionary<SkillId, string>();
            foreach (var attachment in input.Skills)
            {
                ct.ThrowIfCancellationRequested();
                if (prepared.TryGetValue(attachment.Id, out var name))
                {
                    skills.Add(new(attachment.Id, name, Mention: attachment.Mention));
                    continue;
                }
                var skill = catalog.FirstOrDefault(item => item.Id == attachment.Id) ?? throw new PromptSkillNotFoundException(attachment.Id);
                var resources = Path.GetFileName(skill.Location) == "SKILL.md" ? SkillFiles(Path.GetDirectoryName(skill.Location)!, ct) : [];
                skills.Add(new(skill.Id, skill.Name, SkillTool.ToModelOutput(skill, resources), attachment.Mention));
                prepared.Add(skill.Id, skill.Name);
            }
        }
        return new Prompt(input.Text, files, input.Agents?.ToArray(), skills.Count == 0 ? null : skills.ToArray());
    }

    private async Task<PromptFileAttachment> MaterializeAsync(PromptInputFileAttachment input, CancellationToken ct)
    {
        try
        {
            var inline = input.Uri.StartsWith("data:", StringComparison.Ordinal);
            var uri = inline ? null : new Uri(input.Uri, UriKind.Absolute);
            if (uri is not null && uri.Scheme != "file") throw new PromptAttachmentException(input.Uri, $"Unsupported attachment URI: {input.Uri}");
            if (uri is not null && (!OperatingSystem.IsWindows() || uri.IsUnc))
                throw new NotSupportedException("Local prompt files currently require Windows regular-file classification and a non-UNC file URI.");
            var path = uri?.LocalPath;
            var directory = path is not null && (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            if (path is not null && (File.GetAttributes(path) & FileAttributes.Device) != 0)
                throw new PromptAttachmentException(input.Uri, $"Attachment is not a file: {input.Uri}");
            var bytes = inline ? DecodeData(input.Uri) : directory ? DirectoryBytes(path!, ct) : await ReadBytesAsync(path!, input.Uri, ct);
            if (bytes.Length > MaxAttachmentBytes) throw TooLarge(input.Uri);
            var mime = directory ? "application/x-directory" : DetectMime(bytes);
            if (mime == "text/plain" && uri is not null && PositiveQuery(uri, "start") is { } start)
            {
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                var from = (int)Math.Min(start - 1, lines.Length);
                var end = (int)Math.Min(PositiveQuery(uri, "end") ?? lines.Length, lines.Length);
                bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines.Skip(from).Take(Math.Max(0, end - from))));
            }
            var file = new PromptFileAttachment(Convert.ToBase64String(bytes), mime,
                inline ? new PromptInlineFileSource() : new PromptUriFileSource(input.Uri),
                input.Name ?? (path is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(path))), input.Description, input.Mention);
            if (mime.StartsWith("image/", StringComparison.Ordinal) && normalizeImage is not null)
            {
                var normalized = await normalizeImage(file, ct);
                // Image normalization changes only content/mime, never attachment provenance.
                file = file with { Data = normalized.Data, Mime = normalized.Mime };
            }
            if (file.Mime is not ("text/plain" or "application/x-directory" or "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "application/pdf"))
                throw new PromptAttachmentException(input.Uri, $"Unsupported attachment media type: {file.Mime}");
            return file;
        }
        catch (Exception error) when (error is IOException and not PromptAttachmentException or UnauthorizedAccessException or FormatException or DecoderFallbackException or EncoderFallbackException)
        {
            throw new PromptAttachmentException(input.Uri, $"Unable to prepare attachment: {input.Uri}", error);
        }
    }

    private static byte[] DecodeData(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0) throw new PromptAttachmentException(uri, "Invalid attachment data URL");
        var payload = uri[(comma + 1)..];
        if (!uri[..comma].Split(';').Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            // decodeURIComponent rejects malformed escapes and invalid UTF-8, unlike Uri.UnescapeDataString.
            var bytes = new List<byte>();
            for (var index = 0; index < payload.Length;)
            {
                if (payload[index] == '%')
                {
                    if (index + 2 >= payload.Length || !byte.TryParse(payload.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                        throw new PromptAttachmentException(uri, "Invalid attachment data URL");
                    bytes.Add(value);
                    index += 3;
                    continue;
                }
                var end = payload.IndexOf('%', index);
                if (end < 0) end = payload.Length;
                bytes.AddRange(new UTF8Encoding(false, true).GetBytes(payload[index..end]));
                index = end;
            }
            var result = bytes.ToArray();
            _ = new UTF8Encoding(false, true).GetString(result);
            return result;
        }
        var decoded = Convert.FromBase64String(payload);
        if (Convert.ToBase64String(decoded) != payload) throw new PromptAttachmentException(uri, "Non-canonical base64");
        return decoded;
    }

    private static async Task<byte[]> ReadBytesAsync(string path, string uri, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxAttachmentBytes) throw TooLarge(uri);
        return await PipelineBytes.CollectAsync(stream, MaxAttachmentBytes, () => TooLarge(uri), ct);
    }

    private static byte[] DirectoryBytes(string path, CancellationToken ct) => Encoding.UTF8.GetBytes(string.Join('\n',
        new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
            .Where(item => (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0)
            .OrderBy(item => (item.Attributes & FileAttributes.Directory) == 0)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture).Select(item =>
            {
                ct.ThrowIfCancellationRequested();
                return item.Name + ((item.Attributes & FileAttributes.Directory) != 0 ? Path.DirectorySeparatorChar.ToString() : "");
            })));

    private static double? PositiveQuery(Uri uri, string key)
    {
        var value = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .FirstOrDefault(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')) == key);
        return value is { Length: 2 } && double.TryParse(Uri.UnescapeDataString(value[1].Replace('+', ' ')), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number > 0 && Math.Truncate(number) == number ? number : null;
    }

    private static string DetectMime(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (data.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (data.StartsWith("GIF8"u8)) return "image/gif";
        if (data.StartsWith("BM"u8)) return "image/bmp";
        if (data.StartsWith("%PDF-"u8)) return "application/pdf";
        if (data.StartsWith("RIFF"u8) && data.Length >= 12 && data[8..].StartsWith("WEBP"u8)) return "image/webp";
        if (data.Length >= 12 && data[4..].StartsWith("ftyp"u8) && (data[8..].StartsWith("avif"u8) || data[8..].StartsWith("avis"u8))) return "image/avif";
        if (data.Contains((byte)0)) return "application/octet-stream";
        try { _ = new UTF8Encoding(false, true).GetDecoder().GetCharCount(data, false); }
        catch (DecoderFallbackException) { return "application/octet-stream"; }
        return bytes.Length == 0 || bytes.Count(value => value < 9 || value > 13 && value < 32) / (double)bytes.Length <= 0.3
            ? "text/plain" : "application/octet-stream";
    }

    private static IReadOnlyList<string> SkillFiles(string directory, CancellationToken ct)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry.FullName);
                    continue;
                }
                if (entry.Name == "SKILL.md" || (entry.Attributes & FileAttributes.Device) != 0) continue;
                files.Add(OperatingSystem.IsWindows() ? entry.FullName.Replace('\\', '/') : entry.FullName);
                if (files.Count > 10) files.Remove(files.Max!);
            }
        return files.ToArray();
    }

    private static PromptAttachmentException TooLarge(string uri) => new(uri, $"Attachment exceeds the {MaxAttachmentBytes} byte limit: {uri}");
}
