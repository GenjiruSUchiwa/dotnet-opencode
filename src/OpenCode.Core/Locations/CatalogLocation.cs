namespace OpenCode.Core.Locations;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenCode.Core.Database;
using OpenCode.Core.Projects;
using OpenCode.Schema;

public sealed class CatalogLocationUnavailableException(string message) : IOException(message);

/// <summary>Local Project.Current resolution for catalog requests. Does not execute tools or synthesize project/worktree events.</summary>
public static class CatalogLocation
{
    public static Task<LocationInfo> ResolveAsync(IDatabase database, string? directory = null,
        string? workspaceId = null, CancellationToken ct = default)
        => ProjectDiscovery.ResolveAsync(database, directory, workspaceId, ct);

    // Identity discovery is kept internal so Server, SDK and direct Core callers
    // cannot accidentally bypass the Project/worktree announcement boundary.
    internal static async Task<LocationInfo> ResolveIdentityAsync(IDatabase database, string? directory = null,
        string? workspaceId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(workspaceId))
            throw new CatalogLocationUnavailableException("Explicit workspace placement is not implemented by the local catalog location resolver.");
        var requested = RealPath(LocalToolLocation.WindowsPath(directory ?? Directory.GetCurrentDirectory()));
        var root = requested;
        string? marker = null;
        for (var current = new DirectoryInfo(requested); current is not null; current = current.Parent)
        {
            ct.ThrowIfCancellationRequested();
            marker = new[] { ".git", ".hg" }.FirstOrDefault(name => Path.Exists(Path.Combine(current.FullName, name)));
            if (marker is null) continue;
            root = current.FullName;
            break;
        }
        var canonical = root;
        string? vcs = null;
        string id;
        if (marker == ".git")
        {
            var discovery = await RunAsync("git", root, ["rev-parse", "--git-dir", "--git-common-dir", "--show-toplevel"], ct);
            var lines = discovery?.Split('\n').Select(line => OperatingSystem.IsWindows() && line.EndsWith('\r') ? line[..^1] : line).ToArray();
            if (lines is null || lines.Length < 3)
                throw new CatalogLocationUnavailableException("Git repository discovery failed; project identity is unavailable.");
            var gitDirectory = RealPath(Path.GetFullPath(lines[0], root));
            var commonDirectory = RealPath(Path.GetFullPath(lines[1], root));
            root = RealPath(Path.GetFullPath(lines[2], root));
            canonical = root;
            if (!PathEquals(gitDirectory, commonDirectory))
            {
                var worktrees = await RunAsync("git", root, ["worktree", "list", "--porcelain", "-z"], ct);
                var main = worktrees?.Split('\0').FirstOrDefault(record => record.StartsWith("worktree ", StringComparison.Ordinal));
                if (main is null) throw new CatalogLocationUnavailableException("The main Git worktree could not be resolved.");
                canonical = RealPath(Path.GetFullPath(main[9..], root));
            }
            var remote = await RunAsync("git", root, ["remote", "get-url", "origin"], ct);
            var origin = remote is null ? null : NormalizeRemote(remote);
            var cached = await CachedAsync(commonDirectory, ct);
            var roots = origin is null && cached is null
                ? await RunAsync("git", root, ["rev-list", "--max-parents=0", "HEAD"], ct) : null;
            id = origin is not null ? Hash("git-remote:" + origin) : cached
                ?? roots?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).FirstOrDefault()
                ?? "global";
            vcs = "git";
        }
        else if (marker == ".hg")
        {
            var cached = await CachedAsync(Path.Combine(root, ".hg"), ct);
            var roots = cached is null ? await RunAsync("hg", root, ["log", "-r", "roots(all())", "-T", "{node}\n"], ct) : null;
            id = cached ?? roots?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).FirstOrDefault() ?? "global";
            vcs = "hg";
        }
        else id = Hash("directory:" + requested);

        // Identity persistence is followed by ProjectDiscovery's durable worktree
        // announcement/adoption. This internal step does not publish a second event.
        await using var connection = database.CreateConnection();
        await using var db = new OpenCode.Core.Persistence.PersistenceContext(connection);
        await OpenCode.Core.Persistence.SqliteIntrinsics.PutProjectAsync(db, id, StoragePath(canonical), vcs, database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct);
        return new LocationInfo(requested, new LocationProjectInfo(ProjectId.FromExisting(id), root, canonical));
    }

    private static string RealPath(string input)
    {
        var full = Path.GetFullPath(input);
        if (!Directory.Exists(full)) throw new CatalogLocationUnavailableException("The requested location directory is unavailable.");
        var root = Path.GetPathRoot(full)!;
        var current = OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':'
            ? char.ToUpperInvariant(root[0]) + root[1..] : root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = OperatingSystem.IsWindows()
                ? Directory.EnumerateDirectories(current).FirstOrDefault(path => Path.GetFileName(path).Equals(segment, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CatalogLocationUnavailableException("A location path component could not be resolved.")
                : Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (!info.Exists) throw new CatalogLocationUnavailableException("The requested location directory is unavailable.");
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new CatalogLocationUnavailableException("A location link could not be resolved.");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static async Task<string?> CachedAsync(string store, CancellationToken ct)
    {
        var file = Path.Combine(store, "opencode");
        if (!File.Exists(file)) return null;
        var value = (await File.ReadAllTextAsync(file, ct)).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? NormalizeRemote(string input)
    {
        var value = input.Trim();
        string host;
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "file") return null;
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            var match = Regex.Match(value, @"^([^@/:]+@)?([^/:]+):(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            host = match.Groups[2].Value;
            path = match.Groups[3].Value;
        }
        path = Regex.Replace(path.TrimStart('/'), @"\.git/?$", "").TrimEnd('/');
        return host.Length == 0 || path.Length == 0 ? null : host.ToLowerInvariant() + "/" + path;
    }

    private static async Task<string?> RunAsync(string executable, string directory, string[] arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputHandle = input, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            InheritedHandles = []
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            start.KillOnParentExit = true;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (executable == "hg") start.Environment["HGPLAIN"] = "1";
        try
        {
            var output = await Process.RunAndCaptureTextAsync(start, ct);
            // Cancellation while reading throws; cancellation during the final
            // exit wait can instead return a canceled status. Neither is VCS failure.
            ct.ThrowIfCancellationRequested();
            if (output.ExitStatus.Canceled) throw new OperationCanceledException(ct);
            return output.ExitStatus.ExitCode == 0 && output.ExitStatus.Signal is null ? output.StandardOutput : null;
        }
        catch (Win32Exception) { throw new CatalogLocationUnavailableException("The required local VCS executable is unavailable."); }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string StoragePath(string value) => OperatingSystem.IsWindows() ? value.Replace('\\', '/') : value;
    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
