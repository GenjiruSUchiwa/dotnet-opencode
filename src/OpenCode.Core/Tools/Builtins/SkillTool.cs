namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

/// <summary>Location-scoped access to the existing Skill producer's complete catalog, not a second registry.</summary>
public delegate Task<IReadOnlyList<SkillInfo>> ReadSkillCatalog(CancellationToken ct);

public sealed record SkillToolOutput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("output")] string Output);

/// <summary>Source skill leaf: lookup, skill permission, preparation, canonical tool output.
/// The Session owns ordinary tool-result delivery; unlike read, this leaf does not publish a synthetic.</summary>
public sealed class SkillTool
{
    public const string Name = "skill";
    private readonly IToolPermission _permission;
    private readonly ReadSkillCatalog _catalog;

    public SkillTool(IToolPermission permission, ReadSkillCatalog catalog)
    {
        _permission = permission ?? throw new ArgumentNullException(nameof(permission));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ToolInfo Create() => ToolInfo.FromJson(Name,
        "Load a specialized skill's instructions and resources into the current conversation when the task at hand matches its description.\n\nThe skill ID must match an available skill or a skill explicitly referenced by the user.",
        JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"id":{"type":"string","description":"The ID of an available skill or a skill explicitly referenced by the user"}},"required":["id"]}
            """), ExecuteAsync, BuiltinToolSchemas.Skill, new ToolOptions(CodeMode: false));

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var id = new ToolInput(input).String("id");
        ct.ThrowIfCancellationRequested();
        SkillInfo? skill;
        try { skill = (await _catalog(ct)).FirstOrDefault(item => string.Equals(item.Id.Value, id, StringComparison.Ordinal)); }
        catch (IOException error) { throw new ToolExecutionException($"Unable to load skill {id}", error); }
        catch (UnauthorizedAccessException error) { throw new ToolExecutionException($"Unable to load skill {id}", error); }
        if (skill is null) throw new ToolExecutionException($"Unable to load skill {id}");

        // Visibility/autoinvoke controls guidance, not execution authorization. Manual loads still assert the ID.
        try { await _permission.AssertAsync(Name, [skill.Id.Value], [skill.Id.Value], context, null, ct); }
        catch (PermissionBlockedException denial)
        { throw new ToolExecutionException($"Unable to load skill {id}: {denial.Detail}", denial); }
        catch (PermissionCorrectedException correction)
        { throw new ToolExecutionException($"Unable to load skill {id}: {correction.Feedback}", correction); }
        if (!Path.IsPathFullyQualified(skill.Location) || skill.Name is null || skill.Content is null)
            throw new ToolContractException("Skill catalog returned an invalid skill definition.");

        var directory = Path.GetDirectoryName(skill.Location) ?? throw new ToolContractException("Skill location must name a file.");
        IReadOnlyList<string> files;
        try { files = Path.GetFileName(skill.Location) == "SKILL.md" ? SampleFiles(directory, ct) : []; }
        catch (IOException error) { throw new ToolExecutionException($"Unable to load skill {id}", error); }
        catch (UnauthorizedAccessException error) { throw new ToolExecutionException($"Unable to load skill {id}", error); }
        ct.ThrowIfCancellationRequested();
        var output = new SkillToolOutput(skill.Name, directory, ToModelOutput(skill, files));
        return new(output.Output, output, new Dictionary<string, object> { ["name"] = output.Name, ["directory"] = output.Directory });
    }

    public static string ToModelOutput(SkillInfo skill, IReadOnlyList<string> files)
    {
        const string whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        return string.Join('\n', new[]
        {
            $"<skill_content name=\"{skill.Name}\">", $"# Skill: {skill.Name}", "", skill.Content.Trim(whitespace.ToCharArray()), "",
            $"Base directory for this skill: {Path.GetDirectoryName(skill.Location)}",
            "Relative paths in this skill (e.g., scripts/, reference/) are relative to this base directory.",
            "Note: file list is sampled.", "", "<skill_files>"
        }.Concat(files.Select(file => $"<file>{file}</file>")).Concat(["</skill_files>", "</skill_content>"]));
    }

    private static IReadOnlyList<string> SampleFiles(string directory, CancellationToken ct)
    {
        // Source glob("**/*", dot: true, include: file) does not descend through directory symlinks.
        // Retain only the ten smallest absolute paths instead of materializing the full resource list.
        var sample = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        var observed = 0;
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in current.EnumerateFileSystemInfos("*", new EnumerationOptions
                { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++observed > 100_000) throw new IOException("Skill resource enumeration exceeds the local 100000-entry limit.");
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(new DirectoryInfo(entry.FullName));
                        continue;
                    }
                    if (entry.Name == "SKILL.md") continue;
                    var path = OperatingSystem.IsWindows() ? entry.FullName.Replace('\\', '/') : entry.FullName;
                    sample.Add(path);
                    if (sample.Count > 10) sample.Remove(sample.Max!);
                }
            }
            catch (DirectoryNotFoundException) { }
        }
        return sample.ToArray();
    }
}
