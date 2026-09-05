namespace OpenCode.Core.Tools;

using OpenCode.Schema;
using OpenCode.Core.Permissions;

public enum ToolPathKind { File, Directory }
public sealed record ExternalToolDirectory(string Directory, string Resource, string Save);
public sealed record ToolPath(string Absolute, string Resource, ExternalToolDirectory? ExternalDirectory = null);

/// <summary>Location-scoped lexical resolution, including home expansion and project worktree boundaries.
/// Resolution is not authorization. No default implementation is installed.</summary>
public interface IToolLocation
{
    string Directory { get; }
    Task<ToolPath> ResolveAsync(string path, ToolPathKind? kind, CancellationToken ct);
}

/// <summary>The implementation must use the invocation's SessionId, AgentId, MessageId and CallId
/// as the canonical permission source. This service preserves typed policy denial, user decline and cancellation;
/// the calling leaf translates policy denial and correction feedback into declared tool failures.</summary>
public interface IToolPermission
{
    Task AssertAsync(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
        ToolContext context, IReadOnlyDictionary<string, object>? metadata, CancellationToken ct);
}

public sealed record ToolFilePolicy(IToolLocation Location, IToolPermission Permission)
{
    internal async Task<ToolPath> ResolveAsync(string path, ToolPathKind? kind, ToolContext context, CancellationToken ct)
    {
        var target = await Location.ResolveAsync(path, kind, ct).ConfigureAwait(true);
        if (target.ExternalDirectory is { } external)
            await AssertAsync("external_directory", [external.Resource], [external.Save], context, null, ct).ConfigureAwait(true);
        return target;
    }

    internal async Task AssertAsync(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
        ToolContext context, IReadOnlyDictionary<string, object>? metadata, CancellationToken ct)
    {
        try { await Permission.AssertAsync(action, resources, save, context, metadata, ct).ConfigureAwait(true); }
        catch (PermissionBlockedException denial) { throw new ToolExecutionException(denial.Detail, denial); }
        catch (PermissionCorrectedException correction) { throw new ToolExecutionException(correction.Feedback, correction); }
    }
}

/// <summary>Required mutation boundary: serialize cooperating mutations across Locations, preserve UTF-8 BOM,
/// create parents, and apply configured formatting.</summary>
public interface IToolFileMutation
{
    int MaximumBytes { get; }
    Task<IToolFileTransaction> LockAsync(string absolutePath, CancellationToken ct);
    FileDiffInfo Diff(string resource, string before, string after, FileDiffStatus status);
}

public sealed record ToolFileSnapshot(string Text, bool Bom);
public interface IToolFileTransaction : IAsyncDisposable
{
    Task<ToolFileSnapshot?> ReadAsync(CancellationToken ct);
    /// <summary>Call only after the leaf has authorized its preview; checks the read snapshot before writing.</summary>
    Task<string> WriteTextAsync(string content, CancellationToken ct);
}

public interface IToolFileFormatter
{
    /// <summary>Validate and capture matching configuration without executing commands, before the mutation.</summary>
    ValueTask<IToolFileFormatPlan> PrepareAsync(string absolutePath, CancellationToken ct);
}

public interface IToolFileFormatPlan
{
    /// <summary>Run only after approved write, under its mutation lock. True means a formatter exited successfully.</summary>
    Task<bool> ApplyAsync(CancellationToken ct);
}

public sealed record PreparedToolShell(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>Location-scoped shell selection and preparation. Scan the final command after any supported create
/// hooks, resolve cwd and referenced directories, assert external_directory and shell permissions using context,
/// and revalidate cwd after approval. Not an always-allow callback. LocalShellPolicy has no create-hook adapter.</summary>
public interface IToolShellPolicy
{
    Task<PreparedToolShell> PrepareAsync(string command, string? workdir, ToolContext context, CancellationToken ct);
}
