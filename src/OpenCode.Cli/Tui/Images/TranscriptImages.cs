namespace OpenCode.Cli.Tui.Images;

using OpenCode.Schema;

public static class TranscriptImages
{
    public static IReadOnlyList<ImagePreviewItem> User(UserMessage message)
    {
        var seen = new HashSet<(string Mime, string? Name, string? Description, string Mention, string Data)>();
        return (message.Files ?? []).Where(file => file.Mime.StartsWith("image/", StringComparison.Ordinal))
            .Where(file => file.Source is not PromptInlineFileSource || file.Mention?.Text is not { Length: > 0 } mention ||
                seen.Add((file.Mime, file.Name, file.Description, mention, file.Data)))
            // Use admitted bytes, not a re-read of the original URI. This is the
            // source transcript's deduplicateVisibleImages + data-URI mapping.
            .Select(file => new ImagePreviewItem($"data:{file.Mime};base64,{file.Data}")).ToArray();
    }

    public static IReadOnlyList<ImagePreviewItem> Tool(AssistantToolContent part)
    {
        var content = part.State switch
        {
            ToolStateCompleted completed => completed.Content,
            ToolStateError error => error.Content ?? [],
            _ => []
        };
        return content.OfType<ToolFileContent>().Where(file => file.Mime.StartsWith("image/", StringComparison.Ordinal) &&
                file.Uri.StartsWith("data:image/", StringComparison.Ordinal))
            .Select(file => new ImagePreviewItem(file.Uri)).ToArray();
    }
}
