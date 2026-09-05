namespace OpenCode.Cli.Tui.Skills;

using System.Text.RegularExpressions;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

/// <summary>Selection of an actual registered ID. Location/content are metadata, never a file-read substitute.</summary>
public sealed class SkillSelection
{
    internal SkillSelection(LocationRef location, SkillInfo skill) { Location = location; Skill = skill; }
    public LocationRef Location { get; }
    public SkillInfo Skill { get; }
    public PromptInputSkillAttachment ToAttachment(PromptMention? mention = null) => new(Skill.Id, mention);
}

public sealed record SkillCompletion(string Display, string? Description, SkillSelection Selection);

/// <summary>Root-shareable snapshot of the real API response. No global registry, discovery, or arbitrary-path loading.</summary>
public sealed class SkillCatalogSnapshot
{
    private readonly IReadOnlyDictionary<SkillId, SkillInfo> _byId;
    public LocationRef Location { get; }
    public IReadOnlyList<SkillInfo> Skills { get; }

    public SkillCatalogSnapshot(LocationResponse<IReadOnlyList<SkillInfo>> response)
    {
        ArgumentNullException.ThrowIfNull(response.Location);
        ArgumentNullException.ThrowIfNull(response.Data);
        Location = new(response.Location.Directory, response.Location.WorkspaceId);
        Skills = Array.AsReadOnly(response.Data.ToArray());
        if (Skills.Any(skill => skill is null || !skill.Id.IsInitialized() || skill.Name is null || skill.Location is null || skill.Content is null))
            throw new ArgumentException("The skill response is incomplete.", nameof(response));
        _byId = Skills.ToDictionary(skill => skill.Id);
    }

    public SkillSelection Select(SkillId id) => _byId.TryGetValue(id, out var skill) ? new(Location, skill)
        : throw new InvalidOperationException("The selected skill is no longer in the registered catalog.");

    /// <summary>Source @ suggestions include every registered skill; autoinvoke=false does not disable explicit selection.</summary>
    public IReadOnlyList<SkillCompletion> Mentions() => Skills.Select(skill => new SkillCompletion("@" + skill.Id.Value,
        Description(skill), Select(skill.Id))).ToArray();

    /// <summary>Only slash:true skills; actual registered server commands shadow a skill of the same ID.</summary>
    public IReadOnlyList<SkillCompletion> SlashCommands(IReadOnlySet<string> serverCommandNames)
    {
        ArgumentNullException.ThrowIfNull(serverCommandNames);
        return Skills.Where(skill => skill.Slash == true && !serverCommandNames.Any(name => string.Equals(name, skill.Id.Value, StringComparison.Ordinal)))
            .Select(skill => new SkillCompletion("/" + skill.Id.Value, Description(skill), Select(skill.Id)))
            .OrderBy(item => item.Display, StringComparer.CurrentCulture).ToArray();
    }

    public static string? Description(SkillInfo skill) => skill.Description is null ? null : Regex.Replace(skill.Description, @"\s+", " ", RegexOptions.NonBacktracking).Trim();
}
