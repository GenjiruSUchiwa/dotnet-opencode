namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Filesystem;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Route-owned search lifetime. It does not create another Project/Location resolver.</summary>
internal sealed class FilesystemLocations : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileSearchIndex> _indexes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown;
    private readonly TimeProvider _clock;
    private bool _disposed;

    internal FilesystemLocations(CancellationToken stopping, TimeProvider clock)
    { _shutdown = CancellationTokenSource.CreateLinkedTokenSource(stopping); _clock = clock; }

    internal FileSearchIndex Search(LocationInfo location)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Workspace filesystem search is not implemented.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shutdown.Token.ThrowIfCancellationRequested();
            if (_indexes.TryGetValue(location.Directory, out var index)) return index;
            var created = new FileSearchIndex(location, new RipgrepFileScanner(new RipgrepProcess(RipgrepExecutable(), _clock)), _shutdown.Token, _clock);
            _indexes.Add(location.Directory, created);
            return created;
        }
    }

    // Selection only, never downloads or starts a shell. Mirrors the host's supported
    // explicit executable / PATH / existing binary-cache policy without editing ServerHost.
    private static string RipgrepExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        if (Environment.GetEnvironmentVariable("OPENCODE_DOTNET_RIPGREP") is { Length: > 0 } configured)
            return Path.IsPathFullyQualified(configured) && Executable(configured) ? Path.GetFullPath(configured)
                : throw new NotSupportedException("OPENCODE_DOTNET_RIPGREP must name an existing absolute executable.");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        if (!Path.IsPathFullyQualified(cache)) throw new NotSupportedException("XDG_CACHE_HOME must be absolute.");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(cache, "opencode", "bin")))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
            if (Executable(candidate)) return candidate;
        }
        throw new NotSupportedException("Ripgrep is unavailable on PATH and in the existing binary cache. No download or shell fallback was attempted.");
    }

    private static bool Executable(string path) => File.Exists(path) && (OperatingSystem.IsWindows()
        ? Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        : (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    public async ValueTask DisposeAsync()
    {
        FileSearchIndex[] indexes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            indexes = [.. _indexes.Values];
            _indexes.Clear();
        }
        await _shutdown.CancelAsync();
        try { await Task.WhenAll(indexes.Select(index => index.AwaitRefreshAsync())).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        finally { _shutdown.Dispose(); }
    }
}
