namespace OpenTui.Blazor.Clipboard;

using System.Text;
using OpenTui.Native;

public enum ClipboardSelection { Clipboard, Primary }
public enum ClipboardReadStatus { Read, Empty, Unsupported, Cancelled, TimedOut, LimitExceeded, Failed }
public enum ClipboardMutationStatus { Written, Cleared, Unsupported, Cancelled, TimedOut, Failed }
public sealed record ClipboardError(string Message, uint? NativeCode = null);
public sealed record ClipboardReadRequest(IReadOnlyList<string> PreferredTypes, ClipboardSelection Selection = ClipboardSelection.Clipboard);
public sealed record ClipboardFileReference(Uri Uri, string Path);

/// <summary>Managed bytes copied from the completed native operation. No files are opened by representation helpers.</summary>
public sealed record ClipboardRepresentation(string MimeType, ReadOnlyMemory<byte> Bytes)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string ReadText()
    {
        if (!MimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Clipboard representation is not plain text.");
        return Utf8.GetString(Bytes.Span);
    }
    public IReadOnlyList<Uri> ReadUris()
    {
        if (!MimeType.Equals("text/uri-list", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Clipboard representation is not a URI list.");
        return Array.AsReadOnly(Utf8.GetString(Bytes.Span).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => Uri.TryCreate(line, UriKind.Absolute, out var uri) ? uri : throw new FormatException("Clipboard URI list contains an invalid absolute URI.")).ToArray());
    }
    public IReadOnlyList<ClipboardFileReference> ReadFiles() => Array.AsReadOnly(ReadUris().Where(uri => uri.IsFile)
        .Select(uri => new ClipboardFileReference(uri, uri.LocalPath)).ToArray());
    /// <summary>Optional image inspection/presentation through the existing native codec; caller owns the returned handle.</summary>
    public NativeImage DecodeImage()
    {
        if (!MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Clipboard representation is not an image.");
        return NativeImage.Decode(Bytes.Span);
    }
}

public sealed record ClipboardReadResult(ClipboardReadStatus Status, ClipboardRepresentation? Representation = null, ClipboardError? Error = null);
public sealed record ClipboardMutationResult(ClipboardMutationStatus Status, ClipboardError? Error = null);

public interface IClipboardReader
{
    Task<ClipboardReadResult> ReadAsync(ClipboardReadRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Optional richer service. Existing ITextClipboard implementations need not implement this interface.</summary>
public interface IRichClipboard : ITextClipboard, IClipboardReader
{
    Task<ClipboardMutationResult> WriteHostTextAsync(string text, ClipboardSelection selection = ClipboardSelection.Clipboard, CancellationToken cancellationToken = default);
    Task<ClipboardMutationResult> ClearAsync(ClipboardSelection selection = ClipboardSelection.Clipboard, CancellationToken cancellationToken = default);
}
