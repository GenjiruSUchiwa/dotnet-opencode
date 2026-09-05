namespace OpenCode.Cli.Tui.Dialogs;

/// <summary>A real settings destination supplied by the owner, not an invented configuration key.</summary>
public sealed record PaletteSetting(string Id, string Title, string Category, string? Keywords = null);
