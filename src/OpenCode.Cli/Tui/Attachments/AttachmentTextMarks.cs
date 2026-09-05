namespace OpenCode.Cli.Tui.Attachments;

using System.Collections.Immutable;
using OpenCode.Schema;
using OpenTui.Blazor.TextMarks;

public sealed record AttachmentMarkBinding(int Id, AttachmentKind Kind, int Index);
public sealed record AttachmentMarksSnapshot(TerminalTextMarksSnapshot Marks, ImmutableArray<AttachmentMarkBinding> Bindings);

/// <summary>Imports historical/source display-coordinate mentions into a snapshot.
/// This is not a live editor: TextareaState owns all new editing, movement, and history.</summary>
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
