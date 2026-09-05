namespace OpenCode.Cli.Tui.Images;

using System.Net;
using System.Text;
using OpenTui.Native;

/// <summary>Explicit source boundary. A returned media stream transfers disposal to the loader.</summary>
public sealed record ImageSourceAccess(
    Func<string, CancellationToken, Task<Stream?>>? OpenMedia = null,
    Func<string, CancellationToken, Task>? AuthorizeFile = null,
    Func<Uri, CancellationToken, Task>? AuthorizeHttp = null,
    string? BaseDirectory = null);

/// <summary>Reads image bytes only when invoked by an authorized UI action. Native decoding remains a separate step.</summary>
public sealed class ImageSourceLoader(ImageSourceAccess access) : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    public async Task<byte[]> ReadAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return DataUri(source);
        if (access.OpenMedia is not null && await access.OpenMedia(source, cancellationToken) is { } media)
        {
            await using (media) return await ReadBounded(media, cancellationToken);
        }
        var windowsPath = source.Length > 2 && char.IsAsciiLetter(source[0]) && source[1] == ':' && source[2] is '\\' or '/';
        var uri = !windowsPath && Uri.TryCreate(source, UriKind.Absolute, out var absolute) ? absolute : null;
        if (uri?.Scheme is "http" or "https") return await ReadHttp(uri, cancellationToken);
        if (uri is not null && !uri.IsFile) throw new NotSupportedException($"No image source backend is registered for '{uri.Scheme}'.");
        var path = uri?.LocalPath ?? Path.GetFullPath(source, access.BaseDirectory ?? Directory.GetCurrentDirectory());
        if (access.AuthorizeFile is null) throw new UnauthorizedAccessException("Local image reading is not enabled by the host.");
        await access.AuthorizeFile(path, cancellationToken);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
        return await ReadBounded(file, cancellationToken);
    }

    private async Task<byte[]> ReadHttp(Uri uri, CancellationToken cancellationToken)
    {
        if (access.AuthorizeHttp is null) throw new UnauthorizedAccessException("Remote image fetching is not enabled by the host.");
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme is not ("http" or "https")) throw new NotSupportedException("Image redirects must use HTTP or HTTPS.");
            await access.AuthorizeHttp(uri, cancellationToken);
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= 20 || response.Headers.Location is not { } location) throw new HttpRequestException("Image redirect limit reached or redirect location missing.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Failed to fetch image: HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadBounded(stream, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadBounded(Stream stream, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, NativeImage.MaximumEncodedBytes - bytes.Length + 1)), cancellationToken);
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
#pragma warning disable MA0042 // MemoryStream.Write is the bounded in-memory copy, not blocking I/O; preserve the existing cancellation boundary at ReadAsync.
            bytes.Write(buffer, 0, read);
#pragma warning restore MA0042
        }
    }

    private static byte[] DataUri(string source)
    {
        var comma = source.IndexOf(',');
        if (comma < 0) throw new FormatException("Image data URI has no payload separator.");
        var payload = source.AsSpan(comma + 1);
        if (source.AsSpan(5, comma - 5).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Uri.UnescapeDataString(payload.ToString());
            var length = decoded.Count(character => !char.IsWhiteSpace(character));
            if (length > ((NativeImage.MaximumEncodedBytes + 2L) / 3) * 4) throw new NativeImageException(NativeImageStatus.MemoryLimit);
            var result = Convert.FromBase64String(decoded);
            if (result.Length > NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
            return result;
        }
        using var bytes = new MemoryStream();
        Span<byte> encoded = stackalloc byte[4];
        while (!payload.IsEmpty)
        {
            if (payload[0] == '%')
            {
                if (payload.Length < 3 || !byte.TryParse(payload.Slice(1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new FormatException("Invalid image data URI escape.");
                if (bytes.Length == NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
                bytes.WriteByte(value);
                payload = payload[3..];
                continue;
            }
            if (Rune.DecodeFromUtf16(payload, out var rune, out var consumed) != System.Buffers.OperationStatus.Done) throw new FormatException("Invalid Unicode in image data URI.");
            var count = rune.EncodeToUtf8(encoded);
            if (bytes.Length + count > NativeImage.MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
            bytes.Write(encoded[..count]);
            payload = payload[consumed..];
        }
        return bytes.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
