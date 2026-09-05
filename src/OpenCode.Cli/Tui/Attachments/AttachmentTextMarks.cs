namespace OpenCode.Cli.Tui.Attachments;

using System.Collections.Immutable;
using OpenCode.Schema;
using OpenTui.Blazor.TextMarks;

public sealed record AttachmentMarkBinding(int Id, AttachmentKind Kind, int Index);
public sealed record AttachmentMarksSnapshot(TerminalTextMarksSnapshot Marks, ImmutableArray<AttachmentMarkBinding> Bindings);

/// <summary>Owns the relationship between managed marks and actual PromptInput arrays.
/// Text edits change only marker intervals; URI/agent/skill payloads retain their identity.</summary>
public sealed class AttachmentTextMarks
{
    public PromptInput Input { get; private set; }
    public TerminalTextMarks Marks { get; } = new();
    private ImmutableArray<AttachmentMarkBinding> _bindings = [];
    private readonly int _type;

    public AttachmentTextMarks(PromptInput input)
    {
        Input = new("");
        _type = Marks.RegisterType("prompt-part");
        Reconcile(input);
    }

    public AttachmentMarksSnapshot Snapshot() => new(Marks.Snapshot(), _bindings);

    public static AttachmentTextMarks Restore(PromptInput input, AttachmentMarksSnapshot snapshot)
    {
        var state = new AttachmentTextMarks(new("")) { Input = input, _bindings = snapshot.Bindings };
        state.Marks.Restore(snapshot.Marks);
        return state;
    }

    public void Clear()
    {
        Marks.Clear();
        _bindings = [];
        Input = new("");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The source mention-range error describes nested mention data and must keep its existing user-visible text.")]
    public void Reconcile(PromptInput input)
    {
        var used = new HashSet<int>();
        var bindings = ImmutableArray.CreateBuilder<AttachmentMarkBinding>();
        foreach (var part in Parts(input))
        {
            if (part.Mention is not { Text.Length: > 0 } mention) continue;
            var start = Position(mention.Start);
            var end = Position(mention.End);
            if (end < start) throw new ArgumentException("Attachment mention ends before it starts.");
            var previous = _bindings.FirstOrDefault(binding => binding.Kind == part.Kind && !used.Contains(binding.Id)
                && Key(Input, binding.Kind, binding.Index) == part.Key && Marks.Get(binding.Id) is not null);
            var id = previous?.Id ?? Marks.Create(start, end, virtualText: true, typeId: _type, styleKey: Style(part.Kind, part.Key));
            if (previous is not null)
            {
                var mark = Marks.Get(id)!;
                Marks.Update(mark with { Start = start, End = end });
            }
            used.Add(id);
            bindings.Add(new(id, part.Kind, part.Index));
        }
        foreach (var mark in Marks.All.Where(mark => !used.Contains(mark.Id))) Marks.Delete(mark.Id);
        Input = input;
        _bindings = bindings.ToImmutable();
    }

    public AttachmentTextMarks Replace(int start, int length, string inserted, Func<string, int> width)
    {
        var result = Restore(Input, Snapshot());
        if (Marks.All.Count == 0)
        {
            result.Input = Input with { Text = Input.Text.Remove(start, length).Insert(start, inserted) };
            return result;
        }
        var oldMap = new TerminalTextMap(Input.Text, width);
        var first = oldMap.DisplayAtUtf16(start);
        result.Marks.AdjustDeletion(first, oldMap.DisplayAtUtf16(start + length) - first);
        var removed = Input.Text.Remove(start, length);
        var text = removed.Insert(start, inserted);
        if (inserted.Length > 0)
        {
            var insertion = new TerminalTextMap(removed, width).DisplayAtUtf16(start);
            var after = new TerminalTextMap(text, width).DisplayAtUtf16(start + inserted.Length);
            result.Marks.AdjustInsertion(insertion, after - insertion);
        }
        result.Materialize(text);
        return result;
    }

    public IReadOnlyList<TerminalTextMark> Project(Func<string, int> width, Func<AttachmentKind, string?, TerminalTextMarkStyle> style)
    {
        if (_bindings.IsEmpty) return [];
        var map = new TerminalTextMap(Input.Text, width);
        return _bindings.Select(binding => (Binding: binding, Mark: Marks.Get(binding.Id)))
            .Where(item => item.Mark is not null && item.Mark.End > item.Mark.Start)
            .Select(item => map.Project(item.Mark!, style(item.Binding.Kind, item.Mark!.StyleKey))).ToArray();
    }

    private void Materialize(string text)
    {
        var bindings = ImmutableArray.CreateBuilder<AttachmentMarkBinding>();
        var files = new List<PromptInputFileAttachment>();
        var agents = new List<PromptAgentAttachment>();
        var skills = new List<PromptInputSkillAttachment>();
        foreach (var part in Parts(Input))
        {
            var binding = _bindings.FirstOrDefault(binding => binding.Kind == part.Kind && binding.Index == part.Index);
            var mark = binding is null ? null : Marks.Get(binding.Id);
            if (part.Mention is { Text.Length: > 0 } && mark is null) continue;
            var mention = mark is null ? part.Mention : part.Mention! with { Start = mark.Start, End = mark.End };
            var index = part.Kind switch { AttachmentKind.File => files.Count, AttachmentKind.Agent => agents.Count, _ => skills.Count };
            if (mark is not null) bindings.Add(new(mark.Id, part.Kind, index));
            if (part.Kind == AttachmentKind.File) files.Add(Input.Files![part.Index] with { Mention = mention });
            if (part.Kind == AttachmentKind.Agent) agents.Add(Input.Agents![part.Index] with { Mention = mention });
            if (part.Kind == AttachmentKind.Skill) skills.Add(Input.Skills![part.Index] with { Mention = mention });
        }
        Input = Input with { Text = text, Files = Input.Files is null ? null : files.ToArray(),
            Agents = Input.Agents is null ? null : agents.ToArray(), Skills = Input.Skills is null ? null : skills.ToArray() };
        _bindings = bindings.ToImmutable();
    }

    private static IEnumerable<(AttachmentKind Kind, int Index, string Key, PromptMention? Mention)> Parts(PromptInput input) =>
        (input.Files ?? []).Select((file, index) => (AttachmentKind.File, index, file.Uri, file.Mention))
        .Concat((input.Agents ?? []).Select((agent, index) => (AttachmentKind.Agent, index, agent.Name, agent.Mention)))
        .Concat((input.Skills ?? []).Select((skill, index) => (AttachmentKind.Skill, index, skill.Id.Value, skill.Mention)));

    private static string? Key(PromptInput input, AttachmentKind kind, int index) => kind switch
    {
        AttachmentKind.File => input.Files?.ElementAtOrDefault(index)?.Uri,
        AttachmentKind.Agent => input.Agents?.ElementAtOrDefault(index)?.Name,
        _ => input.Skills?.ElementAtOrDefault(index)?.Id.Value
    };
    private static string Style(AttachmentKind kind, string key) => kind == AttachmentKind.File && key.StartsWith("data:", StringComparison.Ordinal) ? "extmark.paste" : kind switch
    { AttachmentKind.File => "extmark.file", AttachmentKind.Agent => "extmark.agent", _ => "extmark.skill" };
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Preserve the editor-display-position error rather than adding a helper parameter name.")]
    private static int Position(double value) => double.IsFinite(value) && value >= 0 && value <= int.MaxValue && Math.Truncate(value) == value
        ? (int)value : throw new ArgumentException("Attachment mention is not an editor-display position.");
}
