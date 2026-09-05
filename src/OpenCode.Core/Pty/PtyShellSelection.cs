namespace OpenCode.Core.Pty;

using OpenCode.Core.Config;
using OpenCode.Core.Locations;
using OpenCode.Schema;

/// <summary>Windows ShellSelect config-priority resolution for basic PTYs, refreshed at creation.</summary>
public static class PtyShellSelection
{
    public static string Resolve(LocationInfo location)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix PTY shell selection and native PTYs are not implemented.");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } root
            ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        if (!Path.IsPathFullyQualified(cache)) throw new NotSupportedException("PTY shell lookup requires an absolute cache directory.");
        var bin = Path.Combine(cache, "opencode", "bin");
        // ConfigShellPlugin uses the latest Location config entry. Config priority does
        // not apply the non-interactive compatibility filter (fish/nu remain eligible).
        var configured = ConfigLoader.LoadDocument(directory: location.Directory)["shell"]?.GetValue<string>();
        var selected = !string.IsNullOrEmpty(configured) ? configured : Environment.GetEnvironmentVariable("SHELL");
        return ResolveExecutable(selected, bin) ?? Find("pwsh", bin) ?? Find("powershell", bin)
            ?? GitBash(bin) ?? ResolveExecutable(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", bin)
            ?? throw new NotSupportedException("No configured or default Windows PTY shell executable is available.");
    }

    /// <summary>Persistent PTY creation uses ShellSelect.environment, not project configuration.</summary>
    public static string ResolveEnvironment()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } root
            ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var bin = Path.Combine(cache, "opencode", "bin");
        if (OperatingSystem.IsWindows())
            return ResolveExecutable(Environment.GetEnvironmentVariable("SHELL"), bin) ?? Find("pwsh", bin) ?? Find("powershell", bin)
                ?? GitBash(bin) ?? ResolveExecutable(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", bin)
                ?? throw new NotSupportedException("No default Windows shell is available.");
        var preferred = Environment.GetEnvironmentVariable("SHELL");
        if (preferred is { Length: > 0 })
        {
            if (Path.IsPathFullyQualified(preferred) && File.Exists(preferred)) return preferred;
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(bin))
                if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, preferred))) return Path.Combine(directory, preferred);
        }
        if (OperatingSystem.IsMacOS()) return "/bin/zsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(bin))
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "bash"))) return Path.Combine(directory, "bash");
        return "/bin/sh";
    }

    private static string? ResolveExecutable(string? value, string bin)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var shell = LocalToolLocation.WindowsPath(value);
        if (Path.GetFileNameWithoutExtension(shell).Equals("bash", StringComparison.OrdinalIgnoreCase)
            && (Path.GetDirectoryName(shell) is "" or null || shell.StartsWith('/')))
            shell = GitBash(bin) ?? shell;
        if (Path.IsPathFullyQualified(shell)) return File.Exists(shell) ? shell : null;
        // Do not resolve relative paths against the daemon's deployment directory.
        if (Path.GetDirectoryName(shell) is not ("" or null))
            throw new NotSupportedException("Relative configured PTY shell paths are not supported; use an absolute executable or a PATH name.");
        return Find(shell, bin);
    }

    private static string? GitBash(string bin)
    {
        var git = Find("git", bin);
        if (git is null) return null;
        var bash = Path.GetFullPath(Path.Combine(git, "..", "..", "bin", "bash.exe"));
        return File.Exists(bash) && new FileInfo(bash).Length > 0 ? bash : null;
    }

    private static string? Find(string name, string bin)
    {
        var extensions = Path.HasExtension(name) ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Append(bin))
        {
            var path = directory.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(path, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
