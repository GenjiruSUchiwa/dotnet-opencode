namespace OpenCode.Core.Instructions;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Agent;
using OpenCode.Core.Config;
using OpenCode.Core.Reference;
using OpenCode.Core.Skill;
using OpenCode.Schema;

internal sealed record LocalInstructionSelection(string System, IReadOnlyList<InstructionSource> Sources);

internal static class LocalInstructions
{
    // Exact runner/prompt/system.txt, with guidance from the captured request definitions.
    private const string SystemPrompt = """
        You are an AI agent powered by OpenCode, a coding agent harness. Help the user accomplish their goals using the tools you have available.

        # Harness
        - Responses are rendered as GitHub-flavored Markdown.
        - `<system-reminder>` blocks are harness instructions, not user-authored content. Read and follow them.
        ${OPENCODE_TOOL_GUIDANCE}

        # Communication
        - Use clear file paths when referring to files.
        - Keep responses clear and concise, and avoid unnecessary technical jargon.

        # Working in codebases
        - Keep changes consistent with the structure, naming, style, and patterns of the surrounding code.
        - Treat unfamiliar files or changes as potential user work and investigate before deleting or overwriting them.
        """;

    internal static async Task<LocalInstructionSelection> ReadAsync(SessionInfo session, string agent, JsonObject config,
        IReadOnlyList<InstructionEntrySnapshot> entries, InstructionLocationState observations, TimeProvider clock, CancellationToken ct,
        AgentInfo? selection = null, IReadOnlyList<string>? toolNames = null, string? projectDirectory = null,
        InstructionSource? mcp = null, InstructionSource? codeMode = null)
    {
        if (session.Location.WorkspaceId is not null) throw new NotSupportedException("Instructions require implicit-local Location placement.");
        var directory = ResolveDirectory(session.Location.Directory);
        var ancestors = Ancestors(directory).ToArray();
        var root = projectDirectory is null
            ? ancestors.FirstOrDefault(path => Exists(Path.Combine(path, ".git"))) ?? directory
            : ResolveDirectory(projectDirectory);
        var home = ResolveDirectory(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var global = Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory());
        var producers = ProducerConfiguration.Read(directory, home, global, config, observations);
        // AgentCatalog owns document normalization and Markdown agent/mode discovery.
        selection ??= await AgentCatalog.ResolveAsync(directory, AgentId.FromExisting(agent), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected agent is unavailable.");
        var system = selection.System is { Length: > 0 } selectedSystem ? selectedSystem : MakeSystem(toolNames ?? []);
        var stop = Contains(home, directory) ? home : root;
        var tmp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode"));
        Directory.CreateDirectory(tmp);
        var sources = new List<InstructionSource>
        {
            Text("core/environment", string.Join("\n", "<env>",
                $"  Current conversation session ID: {session.Id.Value}", $"  Working directory: {directory}",
                $"  Workspace root folder: {root}", $"  Is directory a git repo: {(Exists(Path.Combine(root, ".git")) ? "yes" : "no")}",
                $"  Platform: {(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux")}",
                $"  Prefer {tmp} over generic system temporary directories such as /tmp; it is pre-created and approved for external access.", "</env>"),
                value => "Here is some useful information about the environment you are running in:\n" + value,
                value => "The environment you are running in is now:\n" + value),
            Text("core/date", clock.GetLocalNow().DateTime.ToString("ddd MMM dd yyyy", CultureInfo.InvariantCulture),
                value => "Today's date: " + value, value => "Today's date is now: " + value),
            codeMode ?? CodeModeInstructionSource.Create(null)
        };
        var files = new List<(string Path, string Content)>();
        var available = true;
        try
        {
            // ConfigInstructionPlugin contributes project files only when the
            // Location is inside that project; global instructions remain independent.
            var paths = new[] { Path.Combine(global, "AGENTS.md") }.Concat(Contains(root, directory)
                ? Ancestors(directory).TakeWhile(path => !Same(path, stop)).Append(stop).Select(path => Path.Combine(path, "AGENTS.md"))
                : []);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!Exists(path)) continue;
                var resolved = ResolveFile(path);
                var content = await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
                // Discovery's Map replaces duplicate values without moving their insertion position.
                var index = files.FindIndex(file => Same(file.Path, resolved));
                if (index < 0) files.Add((resolved, content));
                else files[index] = (resolved, content);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { available = false; }
        sources.Add(new InstructionSource("core/instructions", !available ? InstructionAvailability.Unavailable :
            files.Count == 0 ? InstructionAvailability.Removed : InstructionAvailability.Available,
            JsonSerializer.SerializeToElement(files.Select(file => new { path = file.Path, content = file.Content })),
            RenderFiles, ChangedFiles, _ => "Previously loaded instructions no longer apply."));

        // Unsupported instruction producers are not silently treated as empty.
        producers.RequireNoPluginSources();
        foreach (var document in producers.Documents)
            if (Present(document.Info["instructions"]))
                throw new NotSupportedException("Configured instructions requires its native instruction producer before execution.");
        var configuredSkills = producers.Skills();
        SkillSources.RequireLocal(configuredSkills);
        var skills = producers.SkillsAvailable
            ? await SkillSources.ReadAsync(producers.SkillRoots, configuredSkills, directory, home, ct).ConfigureAwait(false)
            : new SkillObservation([], false);
        sources.Add(SkillGuidance.Make(skills.Skills, selection.Permissions, skills.Available,
            canLoadSkills: toolNames?.Contains("skill") == true));
        sources.Add(ReferenceSources.Instructions(ReferenceSources.Observe(producers.Documents, directory, home), producers.ReferencesAvailable));
        sources.Add(mcp ?? McpInstructionSource.WithoutRuntime(producers));
        foreach (var entry in entries)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Key, "^[a-z0-9][a-z0-9._-]*$", System.Text.RegularExpressions.RegexOptions.NonBacktracking)) throw new JsonException("Invalid instruction entry key.");
            string Block(JsonElement value) => "<context key=\"" + entry.Key + "\">\n" +
                (value.ValueKind == JsonValueKind.String ? value.GetString() : InstructionJson.Stringify(value, pretty: true)) + "\n</context>";
            sources.Add(new InstructionSource("api/" + entry.Key,
                entry.Removed ? InstructionAvailability.Removed : InstructionAvailability.Available, entry.Value,
                Block, (_, value) => $"The context under \"{entry.Key}\" changed and supersedes the previous value:\n" + Block(value),
                _ => $"The context under \"{entry.Key}\" no longer applies. Disregard it."));
        }
        // Retain only a fully supported composition, never an unsupported remote
        // source or plugin configuration that failed its producer guard.
        if (producers.ReferencesAvailable) observations.Remember(producers.Documents);
        return new LocalInstructionSelection(system, sources);
    }

    private static string MakeSystem(IReadOnlyList<string> tools)
    {
        var guidance = new List<string>();
        if (tools.Contains("write")) guidance.Add("- Use the write tool to create files or completely replace their content. Prefer using the edit tool for targeted changes.");
        if (tools.Contains("edit")) guidance.Add("- Use the edit tool for targeted changes to existing text files. It replaces the exact text in `oldString` with `newString`, and the values must differ. By default, `oldString` must occur exactly once. If it occurs multiple times, include more surrounding context to make it unique or set `replaceAll` to true to replace every occurrence.");
        if (tools.Contains("read")) guidance.Add("- Prefer using the read tool rather than shell commands like `cat`.");
        return SystemPrompt.Replace("${OPENCODE_TOOL_GUIDANCE}", string.Join("\n", guidance), StringComparison.Ordinal) + "\n";
    }

    private static InstructionSource Text(string key, string value, Func<string, string> initial, Func<string, string> changed) =>
        new(key, InstructionAvailability.Available, JsonSerializer.SerializeToElement(value),
            item => initial(item.GetString()!), (_, item) => changed(item.GetString()!));

    private static InstructionSource Removed(string key, string message) => new(key, InstructionAvailability.Removed, default,
        _ => throw new NotSupportedException($"Stored {key} baseline requires its native renderer."),
        (_, _) => throw new NotSupportedException($"Stored {key} update requires its native renderer."), _ => message);

    private static string RenderFiles(JsonElement files) => string.Join("\n\n", files.EnumerateArray().Select(file =>
        "Instructions from: " + file.GetProperty("path").GetString() + "\n" + file.GetProperty("content").GetString()));

    private static string ChangedFiles(JsonElement before, JsonElement after)
    {
        var previous = before.EnumerateArray().ToDictionary(file => file.GetProperty("path").GetString()!, StringComparer.Ordinal);
        var current = after.EnumerateArray().ToArray();
        var paths = current.Select(file => file.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
        return string.Join("\n\n", previous.Where(file => !paths.Contains(file.Key)).Select(file => $"The instructions from {file.Key} no longer apply.")
            .Concat(current.Where(file => !previous.ContainsKey(file.GetProperty("path").GetString()!)).Select(file =>
                "New instructions apply from:\n" + RenderFiles(JsonSerializer.SerializeToElement(new[] { file }))))
            .Concat(current.Where(file => previous.TryGetValue(file.GetProperty("path").GetString()!, out var old) &&
                old.GetProperty("content").GetString() != file.GetProperty("content").GetString()).Select(file =>
                // Upstream's full-replacement form, without its optional compact diff optimization.
                "The instructions changed:\n" + RenderFiles(JsonSerializer.SerializeToElement(new[] { file })))));
    }

    private static bool Present(JsonNode? value) => value switch { null => false, JsonArray array => array.Count > 0, JsonObject obj => obj.Count > 0, _ => true };
    private static bool Same(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Contains(string root, string path) => Same(root, path) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static IEnumerable<string> Ancestors(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent) yield return directory.FullName;
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static string ResolveDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException("Instruction location is unavailable.");
        foreach (var ancestor in Ancestors(directory.FullName))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != FileAttributes.None)
                throw new NotSupportedException("Instruction discovery through directory links requires canonical Location resolution.");
        return directory.FullName;
    }
    private static string ResolveFile(string path) => new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
}
