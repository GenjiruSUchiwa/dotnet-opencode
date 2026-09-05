namespace OpenCode.Cli.Tui.Attachments;

using System.Collections.Immutable;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.TextMarks;

/// <summary>Immutable application payload. The generic editor treats it as opaque MarkData.</summary>
public sealed record PromptAttachmentData(AttachmentKind Kind, int Order, string Label,
    PromptInputFileAttachment? File = null, PromptAgentAttachment? Agent = null, PromptInputSkillAttachment? Skill = null)
{
    public string Key => File?.Uri ?? Agent?.Name ?? Skill?.Id.Value ?? throw new InvalidOperationException("Missing attachment payload.");
    public string Style => Kind == AttachmentKind.Agent ? "extmark.agent" : Kind == AttachmentKind.Skill ? "extmark.skill"
        : File?.Uri.StartsWith("data:", StringComparison.Ordinal) == true ? "extmark.paste" : "extmark.file";
}

/// <summary>Typed document conversion only: no text editing, word algorithm, width estimate or undo stack.</summary>
public static class PromptDocumentAdapter
{
    public static PromptEditDocument Prepare(PromptEditDocument document, int? cursor = null, int? anchor = null)
    {
        var input = Copy(document.Input);
        var unmarked = document.Unmarked.IsDefault ? Parts(input).Where(part => Mention(part) is not { Text.Length: > 0 }).ToImmutableArray() : document.Unmarked;
        var snapshot = document.Marks ?? new AttachmentTextMarks(input).Snapshot(); // Legacy/source display-coordinate import only.
        var editor = document.Editor ?? new TextareaDocument(input.Text, cursor ?? input.Text.Length, anchor, snapshot.Marks)
        {
            MarkData = snapshot.Bindings.ToImmutableDictionary(binding => binding.Id,
                binding => (object?)Parts(input).First(part => part.Kind == binding.Kind && part.Order == binding.Index))
        };
        if (editor.Text != input.Text) throw new InvalidOperationException("Prompt text and native document disagree.");
        if (editor.Marks is { } marks && (marks.Marks.Any(mark => mark.Id <= 0 || mark.Start < 0 || mark.End < mark.Start)
            || marks.Marks.Select(mark => mark.Id).Distinct().Count() != marks.Marks.Length
            || marks.NextId <= marks.Marks.Select(mark => mark.Id).DefaultIfEmpty(0).Max()))
            throw new InvalidOperationException("The native mark identities/ranges are inconsistent.");
        if (editor.MarkData.Any(pair => pair.Value is not PromptAttachmentData || editor.Marks?.Marks.Any(mark => mark.Id == pair.Key) != true))
            throw new NotSupportedException("A native mark payload cannot be restored losslessly by this prompt adapter.");
        foreach (var part in unmarked.Concat(editor.MarkData.Values.OfType<PromptAttachmentData>()))
            if (part.Order < 0 || part.Label is null || !(part.Kind switch
                {
                    AttachmentKind.File => part.File is not null && part.Agent is null && part.Skill is null,
                    AttachmentKind.Agent => part.Agent is not null && part.File is null && part.Skill is null,
                    AttachmentKind.Skill => part.Skill is not null && part.File is null && part.Agent is null,
                    _ => false
                })) throw new InvalidOperationException("Invalid typed prompt mark payload.");
        if (cursor is { } position) editor = editor with { CursorUtf16 = position, AnchorUtf16 = anchor, Selection = null };
        return document with
        {
            Input = input,
            Metadata = document.Metadata?.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            Unmarked = unmarked,
            Marks = document.Marks ?? snapshot,
            Editor = Freeze(editor)
        };
    }

    public static PromptEditDocument Capture(TextareaDocument source, PromptEditDocument basis, bool shellMode)
    {
        var editor = Freeze(source);
        var parts = basis.Unmarked.IsDefault ? Parts(basis.Input).Where(part => Mention(part) is not { Text.Length: > 0 }).ToImmutableArray() : basis.Unmarked;
        var values = parts.Select(part => (Data: part, Mark: (TerminalExtmark?)null)).Concat((editor.Marks?.Marks ?? [])
            .Where(mark => editor.MarkData.ContainsKey(mark.Id)).Select(mark =>
            {
                if (editor.MarkData[mark.Id] is not PromptAttachmentData data)
                    throw new NotSupportedException("The prompt has an unsupported mark payload; it was not converted to text-only input.");
                return (Data: data, Mark: (TerminalExtmark?)mark);
            })).OrderBy(item => item.Data.Order).ThenBy(item => item.Mark?.Id ?? 0);
        var files = new List<PromptInputFileAttachment>();
        var agents = new List<PromptAgentAttachment>();
        var skills = new List<PromptInputSkillAttachment>();
        var bindings = ImmutableArray.CreateBuilder<AttachmentMarkBinding>();
        foreach (var item in values)
        {
            var mention = item.Mark is { } mark ? new PromptMention(mark.Start, mark.End, item.Data.Label) : Mention(item.Data);
            var index = item.Data.Kind switch { AttachmentKind.File => files.Count, AttachmentKind.Agent => agents.Count, AttachmentKind.Skill => skills.Count,
                _ => throw new NotSupportedException("Unsupported prompt attachment kind.") };
            if (item.Data.File is { } file) files.Add(file with { Mention = mention });
            else if (item.Data.Agent is { } agent) agents.Add(agent with { Mention = mention });
            else if (item.Data.Skill is { } skill) skills.Add(skill with { Mention = mention });
            else throw new InvalidOperationException("A prompt mark has no typed attachment.");
            if (item.Mark is { } bound) bindings.Add(new(bound.Id, item.Data.Kind, index));
        }
        return new(new(editor.Text, basis.Input.Files is null && files.Count == 0 ? null : files.ToImmutableArray(),
            basis.Input.Agents is null && agents.Count == 0 ? null : agents.ToImmutableArray(),
            basis.Input.Skills is null && skills.Count == 0 ? null : skills.ToImmutableArray()), basis.Metadata,
            editor.Marks is { } marks ? new(marks, bindings.ToImmutable()) : null, shellMode) { Editor = editor, Unmarked = parts };
    }

    public static TextareaDocument Freeze(TextareaDocument editor) => editor with
    {
        Marks = editor.Marks is { } marks ? new(marks.Marks.ToImmutableArray(), marks.NextId) : null,
        MarkPositions = editor.MarkPositions?.ToImmutableArray(),
        MarkData = editor.MarkData.ToImmutableDictionary()
    };

    public static PromptInput Copy(PromptInput input) => input with
    { Files = input.Files?.ToImmutableArray(), Agents = input.Agents?.ToImmutableArray(), Skills = input.Skills?.ToImmutableArray() };

    private static IEnumerable<PromptAttachmentData> Parts(PromptInput input) =>
        (input.Files ?? []).Select((file, index) => new PromptAttachmentData(AttachmentKind.File, index, file.Mention?.Text ?? "", File: file))
        .Concat((input.Agents ?? []).Select((agent, index) => new PromptAttachmentData(AttachmentKind.Agent, index, agent.Mention?.Text ?? "", Agent: agent)))
        .Concat((input.Skills ?? []).Select((skill, index) => new PromptAttachmentData(AttachmentKind.Skill, index, skill.Mention?.Text ?? "", Skill: skill)));
    private static PromptMention? Mention(PromptAttachmentData part) => part.File?.Mention ?? part.Agent?.Mention ?? part.Skill?.Mention;
}
