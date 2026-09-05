namespace OpenCode.Core.Tools;

using System.Diagnostics;
using OpenCode.Schema;

/// <summary>Session-owned SessionInstructions.load boundary. The implementation must commit a direct durable
/// synthetic and its instruction.paths metadata, deduplicating against current model-visible history.
/// It must not substitute inbox admission, epoch entries, or a permanent in-memory instruction ledger.</summary>
public delegate Task LoadReadInstructions(SessionId session, IReadOnlyList<string> paths, CancellationToken ct);

/// <summary>The discovery half of core/tool/plugin/read.ts. Does not own Session history or instruction state.</summary>
public sealed class ReadInstructionDiscovery
{
    private readonly IToolLocation _location;
    private readonly LoadReadInstructions _load;
    public ReadInstructionDiscovery(IToolLocation location, LoadReadInstructions load)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public async Task AfterReadAsync(SessionId session, ToolPath target, bool directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (target.ExternalDirectory is not null) return;
        try
        {
            var resolved = LocalToolPath.Resolve(target.Absolute);
            var root = LocalToolPath.Resolve(_location.Directory);
            var candidates = new List<string>();
            for (var current = directory ? resolved : Path.GetDirectoryName(resolved); current is not null; current = Path.GetDirectoryName(current))
            {
                ct.ThrowIfCancellationRequested();
                var candidate = Path.Combine(current, "AGENTS.md");
                if (Path.Exists(candidate))
                {
                    var file = LocalToolPath.Resolve(candidate);
                    if (!Same(Path.GetDirectoryName(file)!, root)) candidates.Add(file);
                }
                if (Same(current, root)) break;
            }
            if (candidates.Count != 0) await _load(session, candidates.AsReadOnly(), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The source makes discovery/load failures best effort after a successful read, including defects.
            // Missing loader wiring is instead rejected at construction, before the file can be read.
            Trace.TraceWarning("Read instruction discovery failed for {0}: {1}", target.Absolute, error.Message);
            // A loader's own timeout is best effort, not cancellation of this read. An
            // actual caller interruption still wins if discovery failed while it arrived.
            ct.ThrowIfCancellationRequested();
        }
    }

    private static bool Same(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
