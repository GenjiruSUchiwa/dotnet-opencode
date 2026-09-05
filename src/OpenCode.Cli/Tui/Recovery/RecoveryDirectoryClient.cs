namespace OpenCode.Cli.Tui.Recovery;

using OpenCode.Client;
using OpenCode.Schema;

/// <summary>Server-backed destination discovery. No client filesystem stat, VCS command, or guessed fallback list.</summary>
public sealed class RecoveryDirectoryClient(Func<CancellationToken, Task<SessionHttpClient>> client)
{
    public async Task<RecoveryDirectoryPage> WorktreesAsync(ProjectId project, LocationRef current, CancellationToken ct)
    {
        if (current.WorkspaceId is not null) throw new NotSupportedException("Recovery from an explicit workspace is not supported by the local move service.");
        var api = await client(ct);
        await api.RefreshWorktreesAsync(project, ct);
        var directories = await api.ListWorktreesAsync(project, ct);
        var root = directories.Where(item => Contains(item.Directory, current.Directory)).OrderByDescending(item => item.Directory.Length).FirstOrDefault()?.Directory;
        return new(directories.OrderBy(item => item.Directory == root ? 0 : 1)
            .ThenBy(item => item.Strategy is null ? 0 : 1).ThenBy(item => item.Strategy is null ? item.Directory.Length : 0)
            .Select(item => new RecoveryDirectoryOption(item.Directory, item.Directory == root ? "Current" : "Other", item.Strategy is not null)).ToArray());
    }

    public async Task<RecoveryDirectoryPage> ChildrenAsync(LocationRef directory, CancellationToken ct)
    {
        if (directory.WorkspaceId is not null) throw new NotSupportedException("Browsing explicit workspace destinations is not supported by this local recovery flow.");
        var api = await client(ct);
        var response = await api.ListFilesAsync(directory: directory.Directory, ct: ct);
        return new(response.Data.Where(entry => entry.Type == FileSystemEntryType.Directory)
            .Select(entry => new RecoveryDirectoryOption(RemoteChild(response.Location.Directory, entry.Path), "Directories")).ToArray(),
            new LocationRef(response.Location.Directory, response.Location.WorkspaceId));
    }

    private static bool Contains(string root, string directory)
    {
        var comparison = root.Length > 1 && root[1] == ':' || root.StartsWith("\\\\", StringComparison.Ordinal)
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = root.Replace('\\', '/').TrimEnd('/');
        var target = directory.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalized, target, comparison) || target.StartsWith(normalized + "/", comparison);
    }

    private static string RemoteChild(string root, string path) => path.StartsWith('/') || path.StartsWith("\\\\", StringComparison.Ordinal)
        || path.Length > 2 && path[1] == ':' ? path : root.TrimEnd('/', '\\') + "/" + path.Replace('\\', '/');
}
