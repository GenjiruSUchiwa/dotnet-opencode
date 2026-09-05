namespace OpenCode.Cli.Tui.Commands;

using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Skills;

public sealed record ComposerAnchor(int X, int Y, int Width, int Height);
public sealed record SlashCommand(string Name, string? Description, string? ClientCommand = null, IReadOnlyList<string>? Aliases = null,
    SkillSelection? Skill = null)
{
    public string Key => (Skill is not null ? "skill:" : ClientCommand is null ? "server:" : "client:") + Name;
}
public sealed record SlashHead(string Name, string Arguments, int End)
{
    // Source parseSlashHead removes exactly one separator, not all argument whitespace.
    public static SlashHead? Parse(string text)
    {
        if (!text.StartsWith('/')) return null;
        var split = Array.FindIndex(text.ToCharArray(), 1, char.IsWhiteSpace);
        return split < 0 ? new(text[1..], "", text.Length) : new(text[1..split], text[(split + 1)..], split);
    }
}

public static class SlashCompletion
{
    public static string? Query(string text, int cursor)
    {
        if (cursor <= 0 || cursor > text.Length || text[0] != '/') return null;
        var value = text[1..cursor];
        return value.Any(char.IsWhiteSpace) || value.Contains('/') ? null : value;
    }

    public static IReadOnlyList<SlashCommand> Filter(IEnumerable<SlashCommand> commands, string query)
    {
        var sorted = commands.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return sorted.ToArray();
        return sorted.Select(command => (Command: command, Score: DialogSearch.Score(query, "/" + command.Name,
                command.Description, string.Join(" ", command.Aliases ?? [])) * (command.Name.StartsWith(query, StringComparison.Ordinal) ? 2 : 1)))
            .Where(item => item.Score > 0).OrderByDescending(item => item.Score).Take(10).Select(item => item.Command).ToArray();
    }
}

/// <summary>UI submission snapshot. The observer owns Session creation and admission observation; this carries no invented inbox ID.</summary>
public sealed record CommandSubmission(SessionId? Session, LocationRef Location, string Command, PromptInput Prompt,
    AgentId? Agent, ModelRef? Model, InboxDeliveryMode Delivery);
