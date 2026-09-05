namespace OpenCode.Core.Tools;

using System.Text;
using OpenCode.Core.Locations;
using OpenCode.Schema;

/// <summary>Explicit local subset: one literal command, no substitutions, pipelines, redirection or control flow.
/// This is not a replacement for the source tree-sitter/portable scanner. Unsupported syntax fails before approval.</summary>
public sealed class LocalLiteralShellPolicy(LocalToolLocation location, IToolPermission permission, string executable) : IToolShellPolicy
{
    public async Task<PreparedToolShell> PrepareAsync(string command, string? workdir, ToolContext context, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("The host must supply an absolute shell executable.", nameof(executable));
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var powershell = name is "powershell" or "pwsh";
        if (!powershell && name is not ("bash" or "dash" or "ksh" or "sh" or "zsh"))
            throw new NotSupportedException("Literal shell preparation supports PowerShell and POSIX-compatible shells only.");
        var words = Parse(command, powershell);
        var target = await location.ResolveAsync(workdir ?? ".", ToolPathKind.Directory, ct).ConfigureAwait(true);
        var directories = new List<ToolPath> { target };
        var head = powershell ? words[0].Value.ToLowerInvariant() : words[0].Value;
        var cwdCommand = head is "cd" or "chdir" or "pushd" or "set-location" or "push-location";
        if (head is "popd" or "pop-location") throw new NotSupportedException("Directory-stack commands require the full shell scanner.");
        if (cwdCommand)
        {
            var index = powershell && words.Count > 1 && words[1].Value.ToLowerInvariant() is "-path" or "-literalpath" ? 2 : 1;
            if (words.Count != index + 1 || words[index].Value.StartsWith('-') || words[index].Value.IndexOfAny(['*', '?']) >= 0)
                throw new NotSupportedException("Directory changes require one explicit literal path.");
            if (words[index].Value.StartsWith('~') && words[index].Value != "~" && !words[index].Value.StartsWith("~/", StringComparison.Ordinal) && !words[index].Value.StartsWith("~\\", StringComparison.Ordinal))
                throw new NotSupportedException("Named-user home expansion requires the full shell scanner.");
            directories.Add(await location.ResolveAsync(location.ResolvePath(words[index].Value, target.Absolute), ToolPathKind.Directory, ct).ConfigureAwait(true));
        }
        var external = directories.Select(directory => directory.ExternalDirectory).OfType<ExternalToolDirectory>()
            .DistinctBy(directory => directory.Resource).ToArray();
        if (external.Length > 0)
            await permission.AssertAsync("external_directory", external.Select(directory => directory.Resource).ToArray(),
                external.Select(directory => directory.Save).ToArray(), context, null, ct).ConfigureAwait(true);
        if (!cwdCommand)
        {
            await permission.AssertAsync("shell", [command.Trim()], [ShellCommandPrefix.Save(words.Select(word => word.Raw).ToArray())], context, null, ct).ConfigureAwait(true);
        }
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(target.Absolute)) throw new DirectoryNotFoundException($"Working directory does not exist: {target.Absolute}");
        if ((File.GetAttributes(executable) & FileAttributes.Directory) != (FileAttributes)0) throw new IOException("Selected shell is not a file.");
        return new(executable, powershell ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command] : ["-c", command], target.Absolute);
    }

    private static List<(string Raw, string Value)> Parse(string command, bool powershell)
    {
        var words = new List<(string Raw, string Value)>();
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && command[index] is ' ' or '\t') index++;
            if (index == command.Length) break;
            var start = index;
            var value = new StringBuilder();
            var quote = '\0';
            while (index < command.Length)
            {
                var character = command[index];
                // Reject, rather than approximate, any syntax that can conceal additional execution or directory changes.
                if ((char.IsControl(character) || char.IsWhiteSpace(character)) && character is not (' ' or '\t') || "$`;|&<>(){}[]#,\u2018\u2019\u201C\u201D".Contains(character) || (!powershell && character == '\\'))
                    throw new NotSupportedException("Command requires the full shell scanner; only literal single commands are supported.");
                if (quote == '\0' && character is ' ' or '\t') break;
                if (character is '\'' or '"')
                {
                    if (quote == '\0')
                    {
                        if (index != start) throw new NotSupportedException("Concatenated shell quoting requires the full scanner.");
                        quote = character;
                    }
                    else if (quote == character)
                    {
                        if (index + 1 < command.Length && command[index + 1] is not (' ' or '\t'))
                            throw new NotSupportedException("Concatenated shell quoting requires the full scanner.");
                        quote = '\0';
                    }
                    else value.Append(character);
                }
                else value.Append(character);
                index++;
            }
            if (quote != '\0') throw new ArgumentException("Unterminated shell quote.", nameof(command));
            words.Add((command[start..index], value.ToString()));
        }
        if (words.Count == 0 || words[0].Value.Length == 0 || words[0].Raw != words[0].Value || words[0].Value.Contains('='))
            throw new NotSupportedException("An unquoted literal command name is required.");
        if (words[0].Value is "if" or "for" or "while" or "until" or "case" or "function" or "foreach" or "switch" or "do" or "!" or "time")
            throw new NotSupportedException("Shell control syntax requires the full shell scanner.");
        return words;
    }
}
