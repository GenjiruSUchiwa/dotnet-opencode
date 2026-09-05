namespace OpenCode.Cli.Commands.Run;

using System.Text;
using OpenCode.Schema;

internal static class RunFiles
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The source-compatible attachment diagnostic already identifies the file and is printed without a C# parameter suffix.")]
    public static async Task<(string? Text, PromptInputFileAttachment? Attachment)> PrepareAsync(string name, string root, CancellationToken ct)
    {
        var path = Path.GetFullPath(name, root);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != FileAttributes.None)
            throw new ArgumentException($"Cannot attach a directory, special file, or file larger than 10 MiB: {name}");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        await using var streamLifetime = stream.ConfigureAwait(true);
        if (!stream.CanSeek || stream.Length > 10 * 1024 * 1024)
            throw new ArgumentException($"Cannot attach a directory, special file, or file larger than 10 MiB: {name}");
        var bytes = new byte[checked((int)stream.Length)];
        var size = 0;
        while (size < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(size), ct).ConfigureAwait(false);
            if (read == 0) break;
            size += read;
        }
        bytes = bytes[..size];
        var mime = RunMimeRegistry.Lookup(path);
        var text = Encoding.UTF8.GetString(bytes);
        var binary = bytes.Contains((byte)0) || bytes.Length != 0 && bytes.Count(value => value < 9 || value > 13 && value < 32) / (double)bytes.Length > 0.3;
        if (!mime.StartsWith("image/", StringComparison.Ordinal) && mime != "application/pdf"
            && !binary && Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(bytes))
            return ($"<file name=\"{Path.GetFileName(path)}\">\n{text}\n</file>", null);
        return (null, new($"data:{mime};base64,{Convert.ToBase64String(bytes)}", Path.GetFileName(path)));
    }
}
