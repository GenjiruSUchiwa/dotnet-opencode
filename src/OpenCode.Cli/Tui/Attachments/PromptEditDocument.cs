namespace OpenCode.Cli.Tui.Attachments;

using System.Text.Json;
using OpenCode.Schema;
using OpenTui.Blazor;
using System.Collections.Immutable;

public sealed record PromptEditDocument(PromptInput Input, IReadOnlyDictionary<string, JsonElement>? Metadata,
    AttachmentMarksSnapshot? Marks = null, bool ShellMode = false)
{
    public TextareaDocument? Editor { get; init; }
    public ImmutableArray<PromptAttachmentData> Unmarked { get; init; }
}
