namespace OpenCode.Cli.Tui.Dialogs;

public sealed record DialogSelectOption<T>(T Value, string Title, string? Description = null, string? Category = null,
    string? Footer = null, string? SearchText = null, string? SearchFooter = null, bool Disabled = false,
    IReadOnlyList<string>? Details = null);

public sealed record DialogSelectAction<T>(string Command, string Title, string Label,
    Func<DialogSelectOption<T>?, Task> Run, bool RequiresSelection = true, bool Disabled = false, bool Right = false);

/// <summary>Resolved semantic colors; the application owns theme selection.</summary>
public sealed record DialogTheme(string Text, string Subdued, string Background, string Backdrop,
    string Category, string FocusedBackground, string FocusedText, string SelectedText,
    string InputBackground, string InputText)
{
    // Compatibility fallback for callers of the pre-existing PickerDialog API.
    public static DialogTheme Existing { get; } = new("#EEEEEE", "#888888", "#202020", "#00000096",
        "#EEEEEE", "#344052", "#EEEEEE", "#EEEEEE", "#202020", "#EEEEEE");
}
