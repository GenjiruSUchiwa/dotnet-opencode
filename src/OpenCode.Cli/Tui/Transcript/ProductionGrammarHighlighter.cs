namespace OpenCode.Cli.Tui.Transcript;

using System.Security.Cryptography;
using System.Text.Json;
using OpenTui.Blazor.Code;

/// <summary>One leased production parser client across transcript code views; no startup WASM execution.</summary>
internal static class ProductionGrammarHighlighter
{
    private static readonly Lock Gate = new();
    // Cache ownership is keyed by the caller's explicit clock, never an ambient
    // global clock shared accidentally by independently composed terminal hosts.
    private static readonly Dictionary<TimeProvider, Pool> Pools = new(ReferenceEqualityComparer.Instance);
    internal sealed class Pool(TimeProvider clock)
    {
        internal readonly Client Client = new(clock);
        internal readonly TimeProvider Clock = clock;
        internal int References;
    }

    internal static Lease Retain(TimeProvider clock)
    {
        lock (Gate)
        {
            if (!Pools.TryGetValue(clock, out var pool)) Pools.Add(clock, pool = new(clock));
            pool.References++;
            return new(pool);
        }
    }

    internal sealed class Lease(Pool pool) : IAsyncDisposable
    {
        private bool _disposed;
        internal ICodeHighlighter Highlighter => pool.Client;
        public async ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (--pool.References != 0) return;
                Pools.Remove(pool.Clock);
            }
            await pool.Client.DisposeAsync();
        }
    }

    internal sealed class Client(TimeProvider clock) : ICodeHighlighter, IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        private TreeSitterHighlighter? _highlighter;
        private TreeSitterGrammarCache? _cache;
        private bool _disposed;

        public async Task<CodeHighlightResult> HighlightAsync(CodeHighlightRequest request, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_highlighter is null)
                {
                    var configuration = await Task.Run(Create, cancellationToken).ConfigureAwait(false);
                    _cache = configuration.Cache;
                    _highlighter = configuration.Highlighter;
                }
                return await _highlighter.HighlightAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return new(false, [], exception.Message); }
            finally { _gate.Release(); }
        }

        private (TreeSitterHighlighter Highlighter, TreeSitterGrammarCache Cache) Create()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Tui", "Transcript", "GrammarAssets");
            var bytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(
                "e72ab19a2007bb64c57431feee11853db2192bb4c834c91254a4259ce1957e2f", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Production grammar manifest integrity failure.");
            var manifest = JsonSerializer.Deserialize<Manifest>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Production grammar manifest is empty.");
            if (manifest.Format != 1) throw new InvalidDataException("Unsupported production grammar manifest format.");
            var registry = new TreeSitterGrammarRegistry();
            foreach (var entry in manifest.Entries)
            {
                var source = TranscriptGrammarCatalog.Sources.Single(item => item.Filetype == entry.Filetype);
                if (!source.Aliases.SequenceEqual(entry.Aliases) || !source.Highlights.SequenceEqual(entry.Highlights.Select(pin => pin.Source)))
                    throw new InvalidDataException($"Production grammar registration differs from source: {entry.Filetype}.");
                var pins = entry.Highlights.Prepend(entry.Wasm).ToDictionary(pin => pin.Source, pin =>
                    new TreeSitterGrammarAsset(pin.Source, pin.Sha256,
                        $"{pin.Source}; source snapshot {manifest.SourceSha256}; SHA-256 {pin.Sha256}",
                        $"{pin.LicenseSource}; SHA-256 {pin.LicenseSha256}; packaged licenses/{pin.LicenseFile}"));
                TranscriptGrammarCatalog.Register(registry, entry.Filetype, entry.LanguageExport, pins);
            }
            // Every asset is already packaged under its digest. A missing/corrupt
            // file fails plain; mutable upstream URLs are never fetched at runtime.
            var origins = manifest.Entries.SelectMany(entry => entry.Highlights.Prepend(entry.Wasm))
                .Select(pin => new Uri(pin.Source.GetLeftPart(UriPartial.Authority))).Distinct();
            var cache = new TreeSitterGrammarCache(Path.Combine(directory, "content"), origins, offline: true);
            return (new TreeSitterHighlighter(registry, cache, clock), cache);
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                try { if (_highlighter is not null) await _highlighter.DisposeAsync().ConfigureAwait(false); }
                finally { if (_cache is not null) await _cache.DisposeAsync().ConfigureAwait(false); }
            }
            finally { _gate.Release(); }
        }
    }

    private sealed record Manifest(int Format, string SourceSha256, Entry[] Entries);
    private sealed record Entry(string Filetype, string LanguageExport, string[] Aliases, Pin Wasm, Pin[] Highlights);
    private sealed record Pin(Uri Source, string Sha256, string LicenseFile, Uri LicenseSource, string LicenseSha256);
}
