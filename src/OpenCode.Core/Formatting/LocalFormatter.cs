namespace OpenCode.Core.Formatting;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Tools;

/// <summary>Local builtins/config formatter producer. No shell fallback, package installer or plugin transform registry.</summary>
public sealed class LocalFormatter : IToolFileFormatter
{
    private readonly TimeProvider _clock;
    private sealed record Definition(string Name, string[] Extensions, string[]? Command, string? Detector = null,
        IReadOnlyDictionary<string, string>? Environment = null)
    {
        public string[]? CachedCommand;
    }
    private readonly string _directory;
    private readonly string _worktree;
    private readonly string? _bin;
    private readonly Lock _gate = new();
    private string? _signature;
    private Definition[] _definitions = [];

    public LocalFormatter(string directory, string worktree, string? bin = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Formatter directories must be absolute.", nameof(directory));
        if (!Path.IsPathFullyQualified(worktree)) throw new ArgumentException("Formatter directories must be absolute.", nameof(worktree));
        if (bin is not null && !Path.IsPathFullyQualified(bin)) throw new ArgumentException("Formatter directories must be absolute.", nameof(bin));
        _directory = directory;
        _worktree = worktree;
        _bin = bin;
    }

    public ValueTask<IToolFileFormatPlan> PrepareAsync(string absolutePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Formatter target must be absolute.", nameof(absolutePath));
        var configured = ConfigLoader.LoadDocument(directory: _directory)["formatter"];
        var signature = configured?.ToJsonString() ?? "null";
        Definition[] matching;
        lock (_gate)
        {
            if (signature != _signature)
            {
                var next = Build(configured);
                _definitions = next;
                _signature = signature;
            }
            matching = _definitions.Where(item => item.Extensions.Contains(Path.GetExtension(absolutePath), StringComparer.Ordinal)).ToArray();
        }
        // Missing npm ownership is a readiness error, not an unformatted successful write.
        foreach (var item in matching.Where(item => item.Detector is "prettier" or "oxfmt" or "biome"))
            throw new NotSupportedException($"Formatter {item.Name} requires the native Npm.which/install lifecycle. Configure an explicit command or disable this formatter before mutation.");
        return ValueTask.FromResult<IToolFileFormatPlan>(new Plan(this, absolutePath, matching));
    }

    private sealed class Plan(LocalFormatter owner, string file, Definition[] matching) : IToolFileFormatPlan
    {
        public async Task<bool> ApplyAsync(CancellationToken ct)
        {
            foreach (var item in matching)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var command = item.CachedCommand;
                    if (command is null)
                    {
                        if (item.Command is null || !owner.Eligible(item, ct)) continue;
                        var executable = item.Detector == "configured" || item.Detector == "pint"
                            ? item.Command[0] : owner.Which(item.Command[0], null, true);
                        if (executable is null) continue;
                        if (item.Detector is "air" or "uv")
                        {
                            var output = new StringBuilder();
                            var probe = item.Detector == "air" ? new[] { executable, "--help" } : [executable, "format", "--help"];
                            var tested = await owner.RunAsync(probe, null, chunk =>
                            {
                                var remaining = 8192 - output.Length;
                                if (remaining > 0) output.Append(chunk.Span[..Math.Min(remaining, chunk.Length)]);
                            }, ct).ConfigureAwait(false);
                            if (tested != 0) continue;
                            if (item.Detector == "air")
                            {
                                var first = output.ToString().Split('\n', 2)[0];
                                if (!first.Contains("R language", StringComparison.Ordinal) || !first.Contains("formatter", StringComparison.Ordinal)) continue;
                            }
                        }
                        command = [executable, .. item.Command.Skip(1)];
                        Interlocked.CompareExchange(ref item.CachedCommand, command, null);
                    }
                    var args = command.Select(argument =>
                    {
                        var index = argument.IndexOf("$FILE", StringComparison.Ordinal);
                        return index < 0 ? argument : argument[..index] + file + argument[(index + 5)..];
                    }).ToArray();
                    if (await owner.RunAsync(args, item.Environment, null, ct).ConfigureAwait(false) == 0) return true;
                    Trace.TraceWarning("Formatter {0} exited unsuccessfully for {1}", item.Name, file);
                }
                catch (Exception error) when (error is IOException or Win32Exception or TimeoutException)
                {
                    Trace.TraceWarning("Formatter {0} failed for {1}: {2}", item.Name, file, error.Message);
                }
            }
            return false;
        }
    }

    private async Task<int> RunAsync(string[] command, IReadOnlyDictionary<string, string>? environment,
        Action<ReadOnlyMemory<char>>? output, CancellationToken ct)
    {
        var executable = command[0].IndexOfAny(['/', '\\']) >= 0 ? Path.GetFullPath(command[0], _directory)
            : Which(command[0], environment, false) ?? command[0];
        var start = new ProcessStartInfo(executable) { WorkingDirectory = _directory };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        // Ordinary formatter output is ignored by source. Only help probes need bounded capture.
        return (await OwnedToolProcess.RunAsync(start, output is null ? null : chunk => { output(chunk); return true; }, 120_000, ct, _clock).ConfigureAwait(false)).ExitCode;
    }

    private bool Eligible(Definition item, CancellationToken ct)
    {
        if (item.Detector is null or "configured" or "air" or "uv") return true;
        if (item.Detector == "clang") return FindUp(".clang-format", ct).Any();
        if (item.Detector == "ocaml") return FindUp(".ocamlformat", ct).Any();
        if (item.Detector == "ruff")
        {
            foreach (var config in new[] { "pyproject.toml", "ruff.toml", ".ruff.toml" })
            {
                var file = FindUp(config, ct).FirstOrDefault();
                if (file is not null && (config != "pyproject.toml" || ReadText(file).Contains("[tool.ruff]", StringComparison.Ordinal))) return true;
            }
            return new[] { "requirements.txt", "pyproject.toml", "Pipfile" }.Any(name =>
                FindUp(name, ct).FirstOrDefault() is { } file && ReadText(file).Contains("ruff", StringComparison.Ordinal));
        }
        foreach (var file in FindUp("composer.json", ct))
        {
            JsonObject? json;
            try { json = JsonNode.Parse(ReadText(file)) as JsonObject; }
            catch (JsonException) { return false; }
            if (json is null) continue;
            if (item.Detector == "pint" && (Has(json, "require", "laravel/pint") || Has(json, "require-dev", "laravel/pint"))) return true;
        }
        return false;
    }

    private static bool Has(JsonObject json, string field, string key) => json[field] is JsonObject entries && entries.ContainsKey(key);

    private IEnumerable<string> FindUp(string name, CancellationToken ct)
    {
        for (var directory = _directory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(directory, name);
            if (Path.Exists(file)) yield return file;
            if (string.Equals(directory, _worktree, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) yield break;
        }
    }

    private static string ReadText(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            var chars = new char[1024 * 1024 + 1];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            if (count == chars.Length) throw new NotSupportedException("Formatter discovery files larger than 1 Mi characters are unsupported.");
            return new string(chars, 0, count);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private string? Which(string name, IReadOnlyDictionary<string, string>? environment, bool appendBin)
    {
        string? Variable(string key) => environment?.FirstOrDefault(pair => string.Equals(pair.Key, key,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value ?? System.Environment.GetEnvironmentVariable(key);
        var path = Variable("PATH") ?? "";
        var paths = path.Length == 0 && appendBin && _bin is not null ? [] : path.Split(Path.PathSeparator).AsEnumerable();
        if (appendBin && _bin is not null) paths = paths.Append(_bin);
        var extensions = OperatingSystem.IsWindows() ? (Variable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';') : [];
        foreach (var directory in paths)
            foreach (var extension in new[] { "" }.Concat(extensions))
            {
                var file = Path.GetFullPath(Path.Combine(directory.Trim('"'), name + extension));
                if (!File.Exists(file)) continue;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == (UnixFileMode)0) continue;
                return file;
            }
        return null;
    }

    private static Definition[] Build(JsonNode? configured)
    {
        if (configured is null || configured is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled) return [];
        var builtins = Builtins();
        if (configured is JsonValue boolean && boolean.TryGetValue<bool>(out var all) && all) return builtins.ToArray();
        if (configured is not JsonObject entries) throw new JsonException("formatter must be a boolean or a formatter map.");
        foreach (var pair in entries)
        {
            if (pair.Value is not JsonObject entry || entry.Any(field => field.Key is not ("disabled" or "command" or "environment" or "extensions")))
                throw new NotSupportedException($"Unsupported formatter configuration: {pair.Key}");
            if (entry.TryGetPropertyValue("disabled", out var disabled))
            {
                if (disabled is not JsonValue value || !value.TryGetValue<bool>(out var off)) throw new JsonException("Formatter disabled must be boolean.");
                if (off) { builtins.RemoveAll(item => item.Name == pair.Key); continue; }
            }
            var index = builtins.FindIndex(item => item.Name == pair.Key);
            var prior = index < 0 ? null : builtins[index];
            var command = entry.ContainsKey("command") ? Strings(entry["command"], "command") : prior?.Command;
            if (command is { Length: 0 } || command is { Length: > 0 } && command[0].Length == 0) throw new JsonException("Formatter command must have an executable.");
            var environment = prior?.Environment?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry.TryGetPropertyValue("environment", out var env))
            {
                if (env is not JsonObject map) throw new JsonException("Formatter environment must be a string map.");
                foreach (var item in map) environment[item.Key] = item.Value is JsonValue text && text.TryGetValue<string>(out var value) ? value : throw new JsonException("Formatter environment values must be strings.");
            }
            var current = new Definition(pair.Key, entry.ContainsKey("extensions") ? Strings(entry["extensions"], "extensions") : prior?.Extensions ?? [],
                command, entry.ContainsKey("command") ? "configured" : prior?.Detector, environment);
            if (index < 0) builtins.Add(current);
            else builtins[index] = current;
        }
        return builtins.ToArray();
    }

    private static string[] Strings(JsonNode? node, string field) => node is JsonArray array
        ? array.Select(item => item is JsonValue text && text.TryGetValue<string>(out var value) ? value : throw new JsonException($"Formatter {field} must contain strings.")).ToArray()
        : throw new JsonException($"Formatter {field} must be a string array.");

    private static List<Definition> Builtins()
    {
        var web = ".js .jsx .mjs .cjs .ts .tsx .mts .cts .html .htm .css .scss .sass .less .vue .svelte .json .jsonc .yaml .yml .toml .xml .md .mdx .graphql .gql".Split(' ');
        var bun = new Dictionary<string, string> { ["BUN_BE_BUN"] = "1" };
        return
        [
            new("gofmt", [".go"], ["gofmt", "-w", "$FILE"]),
            new("mix", ".ex .exs .eex .heex .leex .neex .sface".Split(' '), ["mix", "format", "$FILE"]),
            new("oxfmt", ".js .jsx .mjs .cjs .ts .tsx .mts .cts".Split(' '), ["oxfmt", "$FILE"], "oxfmt", bun),
            new("prettier", web, ["prettier", "--write", "$FILE"], "prettier", bun),
            new("biome", web, ["biome", "format", "--write", "$FILE"], "biome", bun),
            new("zig", [".zig", ".zon"], ["zig", "fmt", "$FILE"]),
            new("clang-format", ".c .cc .cpp .cxx .c++ .h .hh .hpp .hxx .h++ .ino .C .H".Split(' '), ["clang-format", "-i", "$FILE"], "clang"),
            new("ktlint", [".kt", ".kts"], ["ktlint", "-F", "$FILE"]),
            new("ruff", [".py", ".pyi"], ["ruff", "format", "$FILE"], "ruff"),
            new("air", [".R"], ["air", "format", "$FILE"], "air"),
            new("uv", [".py", ".pyi"], ["uv", "format", "--", "$FILE"], "uv"),
            new("rubocop", ".rb .rake .gemspec .ru".Split(' '), ["rubocop", "--autocorrect", "$FILE"]),
            new("standardrb", ".rb .rake .gemspec .ru".Split(' '), ["standardrb", "--fix", "$FILE"]),
            new("htmlbeautifier", [".erb", ".html.erb"], ["htmlbeautifier", "$FILE"]),
            new("dart", [".dart"], ["dart", "format", "$FILE"]),
            new("ocamlformat", [".ml", ".mli"], ["ocamlformat", "-i", "$FILE"], "ocaml"),
            new("terraform", [".tf", ".tfvars"], ["terraform", "fmt", "$FILE"]),
            new("latexindent", [".tex"], ["latexindent", "-w", "-s", "$FILE"]),
            new("gleam", [".gleam"], ["gleam", "format", "$FILE"]),
            new("shfmt", [".sh", ".bash"], ["shfmt", "-w", "$FILE"]),
            new("nixfmt", [".nix"], ["nixfmt", "$FILE"]),
            new("rustfmt", [".rs"], ["rustfmt", "$FILE"]),
            new("pint", [".php"], ["./vendor/bin/pint", "$FILE"], "pint"),
            new("ormolu", [".hs"], ["ormolu", "-i", "$FILE"]),
            new("cljfmt", ".clj .cljs .cljc .edn".Split(' '), ["cljfmt", "fix", "--quiet", "$FILE"]),
            new("dfmt", [".d"], ["dfmt", "-i", "$FILE"])
        ];
    }
}
