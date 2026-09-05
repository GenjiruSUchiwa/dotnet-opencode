namespace OpenCode.Cli.Tui.Attachments;

using System.Text.Json;
using OpenCode.Schema;

public sealed record PromptEditDocument(PromptInput Input, IReadOnlyDictionary<string, JsonElement>? Metadata,
    AttachmentMarksSnapshot? Marks = null, bool ShellMode = false);
