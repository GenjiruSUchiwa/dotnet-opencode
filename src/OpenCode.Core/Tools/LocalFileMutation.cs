namespace OpenCode.Core.Tools;
using Transport;

using System.Text;
using OpenCode.Schema;

/// <summary>Local cooperating mutation transactions. Not an OS sandbox or an atomic transaction with external writers.</summary>
public sealed class LocalFileMutation : IToolFileMutation
{
    private readonly IToolFileFormatter? _formatter;
    private readonly int _maximumBytes;
    public int MaximumBytes => _maximumBytes;
    public LocalFileMutation(IToolFileFormatter? formatter = null, int maximumBytes = 20 * 1024 * 1024)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _formatter = formatter;
        _maximumBytes = maximumBytes;
    }
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Locks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<IToolFileTransaction> LockAsync(string absolutePath, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Mutation paths must be absolute.");
        ct.ThrowIfCancellationRequested();
        var key = LocalToolPath.Resolve(absolutePath);
        Entry entry;
        lock (Gate)
        {
            if (!Locks.TryGetValue(key, out entry!)) Locks[key] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(ct); }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
        try
        {
            var plan = _formatter is null ? null : await _formatter.PrepareAsync(absolutePath, ct);
            return new Transaction(absolutePath, key, entry, plan, _maximumBytes);
        }
        catch
        {
            entry.Semaphore.Release();
            ReleaseReference(key, entry);
            throw;
        }
    }

    public FileDiffInfo Diff(string resource, string before, string after, FileDiffStatus status) => ToolTextDiff.Create(resource, before, after, status);

    private static void ReleaseReference(string key, Entry entry)
    {
        lock (Gate)
        {
            if (--entry.Users != 0) return;
            Locks.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Transaction(string path, string key, Entry entry, IToolFileFormatPlan? formatter, int maximumBytes) : IToolFileTransaction
    {
        private byte[]? _original;
        private bool _read;
        private bool _written;
        private int _disposed;

        public async Task<ToolFileSnapshot?> ReadAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_read) throw new InvalidOperationException("Mutation snapshot was already read.");
            _original = await ReadBytesAsync(ct);
            _read = true;
            if (_original is null) return null;
            var decoded = Encoding.UTF8.GetString(_original);
            return new(decoded.TrimStart('\uFEFF'), decoded.StartsWith('\uFEFF'));
        }

        public async Task<string> WriteTextAsync(string content, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_read || _written) throw new InvalidOperationException("A mutation requires one read snapshot and one write.");
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(LocalToolPath.Resolve(path), key, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ToolExecutionException("Mutation target changed while awaiting approval. Read and approve it again.");
            var current = await ReadBytesAsync(ct);
            if ((_original is null) != (current is null) || (_original is not null && !_original.AsSpan().SequenceEqual(current)))
                throw new ToolExecutionException("File changed while awaiting approval. Read and approve it again.");
            var bom = content.StartsWith('\uFEFF') || (_original is { Length: >= 3 } && _original[0] == 0xef && _original[1] == 0xbb && _original[2] == 0xbf);
            var next = content.TrimStart('\uFEFF');
            if ((long)Encoding.UTF8.GetByteCount(next) + (bom ? 3 : 0) > maximumBytes)
                throw new ToolExecutionException($"Mutation exceeds the configured {maximumBytes} byte limit.");
            ct.ThrowIfCancellationRequested();
            _written = true;
            // Once a write starts, settle it and BOM restoration before releasing the transaction lock.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + next), CancellationToken.None);
            if (formatter is not null)
            {
                var formattedSuccessfully = await formatter.ApplyAsync(CancellationToken.None);
                var formatted = Encoding.UTF8.GetString(await ReadBytesAsync(CancellationToken.None)
                    ?? throw new IOException("Formatter removed the mutation target."));
                next = formatted.TrimStart('\uFEFF');
                var canonical = (bom ? "\uFEFF" : "") + next;
                if (formattedSuccessfully && formatted != canonical) await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(canonical), CancellationToken.None);
            }
            return next;
        }

        private async Task<byte[]?> ReadBytesAsync(CancellationToken ct)
        {
            if (Directory.Exists(path)) throw new IOException($"Path is a directory, not a file: {path}");
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > maximumBytes) throw new ToolExecutionException($"Mutation snapshot exceeds the configured {maximumBytes} byte limit.");
                return await PipelineBytes.CollectAsync(file, maximumBytes,
                    () => new ToolExecutionException($"Mutation snapshot exceeds the configured {maximumBytes} byte limit."), ct);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            entry.Semaphore.Release();
            ReleaseReference(key, entry);
            return ValueTask.CompletedTask;
        }
    }
}
