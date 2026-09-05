namespace OpenCode.Cli.Tui.Images;

public sealed record ImagePreviewRequest(IReadOnlyList<ImagePreviewItem> Images, int Initial = 0);
