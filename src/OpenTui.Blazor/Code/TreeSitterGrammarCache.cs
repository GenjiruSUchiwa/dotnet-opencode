namespace OpenTui.Blazor.Code;
using Transport;

using System.Security.Cryptography;
using System.Text;

/// <summary>Explicit content-addressed cache. Construction does not read files or contact a server.</summary>
public sealed class TreeSitterGrammarCache : IAsyncDisposable
{
    private readonly string _directory;
    private readonly HashSet<string> _origins;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private readonly bool _offline;

    /// <param name="allowedOrigins">Exact HTTPS origins, including any authorized redirect/CDN origins.</param>
    public TreeSitterGrammarCache(string directory, IEnumerable<Uri> allowedOrigins, bool offline = false, TimeProvider? clock = null)
    {
        _directory = Path.GetFullPath(directory);
        _offline = offline;
        _origins = allowedOrigins.Select(origin => origin.Scheme == "https" && origin.AbsolutePath == "/" &&
            origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0
            ? origin.GetLeftPart(UriPartial.Authority) : throw new ArgumentException("Allow-list entries must be HTTPS origins.", nameof(allowedOrigins))).ToHashSet(StringComparer.Ordinal);
        _clock = clock ?? TimeProvider.System;
        _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<byte[]> ReadAsync(TreeSitterGrammarAsset asset, int limit, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Authorization applies even to cache hits, not only network requests.
            if (!asset.Source.IsFile) Authorize(asset.Source);
            var path = Path.Combine(_directory, asset.Sha256.ToLowerInvariant());
            if (File.Exists(path))
            {
                await using var cached = File.OpenRead(path);
                return Verify(await ReadBounded(cached, limit, cancellation).ConfigureAwait(false), asset);
            }
            if (_offline) throw new FileNotFoundException($"Packaged grammar asset is missing ({asset.Provenance}).", path);
            var bytes = asset.Source.IsFile
                ? await ReadLocal(asset.Source.LocalPath, limit, cancellation).ConfigureAwait(false)
                : await Download(asset.Source, limit, cancellation).ConfigureAwait(false);
            Verify(bytes, asset);
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                // Another cache owner may publish the same immutable digest concurrently.
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path))
                {
                    await using var winner = File.OpenRead(path);
                    Verify(await ReadBounded(winner, limit, cancellation).ConfigureAwait(false), asset);
                }
            }
            finally { File.Delete(temporary); }
            return bytes;
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> QueriesAsync(IReadOnlyList<TreeSitterGrammarAsset> assets, CancellationToken cancellation)
    {
        var queries = new List<string>();
        foreach (var asset in assets)
        {
            var bytes = await ReadAsync(asset, 8 * 1024 * 1024, cancellation).ConfigureAwait(false);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (!string.IsNullOrWhiteSpace(text)) queries.Add(text);
        }
        return string.Join("\n", queries);
    }

    private void Authorize(Uri uri)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !_origins.Contains(uri.GetLeftPart(UriPartial.Authority)))
            throw new InvalidOperationException($"Grammar asset origin is not authorized: {uri.GetLeftPart(UriPartial.Authority)}");
    }

    private async Task<byte[]> Download(Uri source, int limit, CancellationToken cancellation)
    {
        var current = source;
        for (var hop = 0; hop < 6; hop++)
        {
            Authorize(current);
            using var response = await GetHeadersAsync(current, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                current = response.Headers.Location is { } location ? new Uri(current, location) : throw new InvalidDataException("Asset redirect has no location.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Grammar asset exceeds its size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            return await ReadBounded(stream, limit, cancellation).ConfigureAwait(false);
        }
        throw new InvalidDataException("Too many grammar asset redirects.");
    }

    private async Task<HttpResponseMessage> GetHeadersAsync(Uri source, CancellationToken cancellation)
    {
        // Preserve HttpClient's former 100-second, per-request headers deadline.
        // Body reads remain caller-cancelled under ResponseHeadersRead semantics.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        return await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLocal(string path, int limit, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        return await ReadBounded(stream, limit, cancellation).ConfigureAwait(false);
    }

    private static Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation) =>
        PipelineBytes.CollectAsync(stream, limit, () => new InvalidDataException("Grammar asset exceeds its size limit."), cancellation);

    private static byte[] Verify(byte[] bytes, TreeSitterGrammarAsset asset) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)
            ? bytes : throw new InvalidDataException($"Grammar asset integrity failure ({asset.Provenance}).");

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _http.Dispose(); }
        finally { _gate.Release(); }
    }
}
