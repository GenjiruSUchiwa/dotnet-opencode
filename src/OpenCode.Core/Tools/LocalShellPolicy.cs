namespace OpenCode.Core.Tools;

using OpenCode.Core.Config;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

/// <summary>Local shell source: select, scan the entire command, authorize directories and command spans,
/// then revalidate cwd. It never starts a shell to parse or discover an executable.</summary>
public sealed class LocalShellPolicy(LocalToolLocation location, IToolPermission permission, string? executable = null) : IToolShellPolicy
{
    public const string Grammar = "Bounded non-executing shell analysis supports literal command names, quoted/escaped arguments, scalar/environment assignments, scanned substitutions, pipelines, &&, ||, lists and file/stream redirection. " +
        "POSIX supports array construction/element assignments and common array expansions, if/elif/else, while/until, word-list for/select, arithmetic for headers, arithmetic expansions/commands, case, functions, groups, backticks, process substitutions, parameter defaults, here-documents and here-strings. " +
        "PowerShell supports scalar/member/index/type expressions, array/hashtable expressions, named argument splatting, typed catches and parameter lists, if/elseif/else, while/do, for/foreach, parenthesized switch, functions, here-strings and scanned script-block arguments. " +
        "Literal here-bodies stay data; expandable bodies have their command substitutions scanned. Redirect-only, assignment and compound redirects retain separate shell permission resources. " +
        "All bodies are scanned without evaluating branches or propagating assigned values. Directory changes require explicit literal paths. " +
        "Arithmetic is scanned for nested commands without evaluating values. PowerShell member/type/array expressions retain raw shell approval resources; no CLR types or values are evaluated. " +
        "PowerShell --% retains a literal tail until newline or an unquoted pipe; tail text is not scanned as commands. " +
        "This is not a full shell parser: dynamic command/member names, switch file input, nested POSIX array constructors, PowerShell class declarations and background jobs are not supported. Unclassified syntax is rejected before execution.";

    public async Task<PreparedToolShell> PrepareAsync(string command, string? workdir, ToolContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var shell = Select();
        var powershell = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant() is "powershell" or "pwsh";
        var scanned = new ShellSyntaxScanner(command, powershell, ct).Scan();
        var target = await location.ResolveAsync(workdir ?? ".", ToolPathKind.Directory, ct).ConfigureAwait(true);
        var directories = new List<ToolPath> { target };
        var commands = new List<ScannedShellCommand>();
        foreach (var item in scanned)
        {
            // Redirect-only/compound resources represent real file side effects,
            // not a fabricated executable that could inherit a command-prefix grant.
            if (item.Words.Count == 0) { commands.Add(item); continue; }
            var name = powershell ? item.Words[0].Value.ToLowerInvariant() : item.Words[0].Value;
            if (name is "popd" or "pop-location") throw new ToolExecutionException("Directory-stack restoration is not supported. Use workdir or an explicit path.");
            if (name is not ("cd" or "chdir" or "pushd" or "set-location" or "push-location")) { commands.Add(item); continue; }
            var index = powershell && item.Words.Count > 1 && item.Words[1].Value.ToLowerInvariant() is "-path" or "-literalpath" ? 2 : 1;
            if (item.Words.Count != index + 1 || !item.Words[index].Literal || item.Words[index].Value.Length == 0 ||
                item.Words[index].Value.StartsWith('-') || item.Words[index].Value.IndexOfAny(['*', '?', '[', ']']) >= 0)
                throw new ToolExecutionException("Directory changes require one explicit literal path, optionally with -Path or -LiteralPath in PowerShell. Use workdir for other cases.");
            var path = item.Words[index].Value;
            if (path.StartsWith('~') && path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal))
                throw new ToolExecutionException("Named-user home expansion is not supported. Supply an explicit path.");
            // Match source resolution against the invocation cwd, not an invented shell interpreter state.
            directories.Add(await location.ResolveAsync(location.ResolvePath(path, target.Absolute), ToolPathKind.Directory, ct).ConfigureAwait(true));
            // Unlike an ordinary cd, a redirect has a separate file side effect and needs shell approval.
            if (item.Redirected) commands.Add(item);
        }
        var external = directories.Select(directory => directory.ExternalDirectory).OfType<ExternalToolDirectory>()
            .DistinctBy(directory => directory.Resource).ToArray();
        if (external.Length > 0)
            await permission.AssertAsync("external_directory", external.Select(directory => directory.Resource).ToArray(),
                external.Select(directory => directory.Save).ToArray(), context, null, ct).ConfigureAwait(true);
        if (commands.Count > 0)
            await permission.AssertAsync("shell", commands.Select(item => item.Resource).ToArray(),
                commands.Select(item => item.ExactGrant
                    ? item.Resource.IndexOfAny(['*', '?']) < 0 ? item.Resource : null
                    : ShellCommandPrefix.Grant(item)).OfType<string>().ToArray(), context, null, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(target.Absolute)) throw new DirectoryNotFoundException($"Working directory does not exist: {target.Absolute}");
        if (!File.Exists(shell)) throw new FileNotFoundException("Selected shell no longer exists.", shell);
        return new(shell, powershell ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command] : ["-c", command], target.Absolute);
    }

    private string Select()
    {
        if (executable is not null)
        {
            if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("The host must supply an absolute shell executable.", nameof(executable));
            return Require(executable);
        }
        var configured = ConfigLoader.LoadDocument(directory: location.Directory)["shell"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(configured)) return Require(configured);
        var environment = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(environment) && Supported(environment) && Find(environment) is { } preferred) return preferred;
        var candidates = OperatingSystem.IsWindows() ? new[] { "pwsh", "powershell", "bash" }
            : OperatingSystem.IsMacOS() ? ["/bin/zsh", "bash", "/bin/sh"] : ["bash", "/bin/sh"];
        foreach (var candidate in candidates)
            if (Find(candidate) is { } found) return found;
        if (OperatingSystem.IsWindows())
        {
            if (Find("git") is { } git && Find(Path.GetFullPath(Path.Combine(git, "..", "..", "bin", "bash.exe"))) is { } bash) return bash;
            var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (Find(windowsPowerShell) is { } legacy) return legacy;
        }
        throw new ToolExecutionException("No supported shell was found. Install PowerShell, bash, dash, ksh, sh or zsh, or configure an absolute shell path. cmd, fish and nu are not supported by this scanner.");
    }

    private static string Require(string name)
    {
        if (!Supported(name)) throw new ToolExecutionException($"Shell '{name}' is not supported by the native scanner. Supported shells: pwsh, powershell, bash, dash, ksh, sh, zsh.");
        return Find(name) ?? throw new ToolExecutionException($"Configured shell was not found: {name}");
    }

    private static bool Supported(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() is
        "pwsh" or "powershell" or "bash" or "dash" or "ksh" or "sh" or "zsh";

    private static string? Find(string name)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? Path.GetFullPath(name) : null;
        // Do not resolve executables relative to process-global cwd or the writable project directory.
        if (name.Contains('/') || name.Contains('\\')) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var root = directory.Trim('"');
            if (!Path.IsPathFullyQualified(root)) continue;
            var file = Path.Combine(root, OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name);
            if (File.Exists(file)) return Path.GetFullPath(file);
        }
        return null;
    }
}
