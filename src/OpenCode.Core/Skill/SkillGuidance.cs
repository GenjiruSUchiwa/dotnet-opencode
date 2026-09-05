namespace OpenCode.Core.Skill;

using System.Text.Json;
using OpenCode.Core.Instructions;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

internal static class SkillGuidance
{
    internal static InstructionSource Make(IReadOnlyList<SkillInfo> skills, IReadOnlyList<PermissionRule> permissions,
        bool available, bool canLoadSkills)
    {
        var summaries = skills.Where(skill => PermissionRules.Evaluate("skill", skill.Id.Value, permissions).Effect != PermissionEffect.Deny)
            .Where(skill => skill.Description is not null && skill.Autoinvoke != false)
            .OrderBy(skill => skill.Id.Value, StringComparer.CurrentCulture)
            .Select(skill => new { id = skill.Id.Value, name = skill.Name, description = skill.Description }).ToArray();
        if (!canLoadSkills && summaries.Length > 0)
            throw new NotSupportedException("Visible local skills require an actual skill-loading tool capability before their guidance can be sent to the model.");
        return new InstructionSource("core/skill-guidance", !available ? InstructionAvailability.Unavailable :
            summaries.Length == 0 ? InstructionAvailability.Removed : InstructionAvailability.Available,
            JsonSerializer.SerializeToElement(summaries), value =>
            {
                if (!available && !canLoadSkills && value.GetArrayLength() > 0)
                    throw new NotSupportedException("Retained skill guidance requires a skill-loading tool capability that is unavailable.");
                return Render(value);
            }, Update,
            _ => "Skill guidance is no longer available. Do not use any previously listed skill.");
    }

    private static IEnumerable<string> Entries(IEnumerable<JsonElement> skills) => skills.SelectMany(skill => new[]
    {
        "  <skill>", $"    <id>{skill.GetProperty("id").GetString()}</id>",
        $"    <name>{skill.GetProperty("name").GetString()}</name>",
        $"    <description>{skill.GetProperty("description").GetString()}</description>", "  </skill>"
    });

    private static string Render(JsonElement value) => string.Join("\n", new[]
    {
        "Skills provide specialized instructions and workflows for specific tasks.",
        "Use the skill tool to load a skill when a task matches its description."
    }.Concat(value.GetArrayLength() == 0 ? ["No skills are currently available."] :
        new[] { "<available_skills>" }.Concat(Entries(value.EnumerateArray())).Append("</available_skills>")));

    private static string Update(JsonElement previous, JsonElement current)
    {
        var before = previous.EnumerateArray().ToDictionary(value => value.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var after = current.EnumerateArray().ToDictionary(value => value.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var added = after.Where(value => !before.ContainsKey(value.Key)).Select(value => value.Value).ToArray();
        var removed = before.Keys.Where(key => !after.ContainsKey(key)).ToArray();
        if (after.Any(value => before.TryGetValue(value.Key, out var old) &&
            (old.GetProperty("name").GetString() != value.Value.GetProperty("name").GetString() ||
             old.GetProperty("description").GetString() != value.Value.GetProperty("description").GetString())) ||
            added.Length == 0 && removed.Length == 0)
            return "The available skills have changed. This list supersedes the previous available skills list.\n" + Render(current);
        return string.Join("\n", (added.Length == 0 ? Array.Empty<string>() :
            new[] { "New skills are available in addition to those previously listed:" }.Concat(Entries(added)))
            .Concat(removed.Length == 0 ? Array.Empty<string>() :
                [$"The following skill IDs are no longer available and must not be used: {string.Join(", ", removed)}."]));
    }
}
