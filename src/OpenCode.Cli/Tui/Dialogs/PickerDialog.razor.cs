namespace OpenCode.Cli.Tui.Dialogs;

using Microsoft.AspNetCore.Components;
using OpenTui.Blazor.Components;

public sealed record PickerOption<T>(T Value, string Title, string? Description = null, string? Category = null, bool Disabled = false);

/// <summary>Compatibility facade over the shared source-shaped selector.</summary>
public partial class PickerDialog<T>
{
    [Parameter] public string Title { get; set; } = "Select";
    [Parameter] public IReadOnlyList<PickerOption<T>> Options { get; set; } = [];
    [Parameter] public T? Current { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public DialogTheme Theme { get; set; } = DialogTheme.Existing;
    [Parameter] public IReadOnlyList<DialogSelectAction<T>> Actions { get; set; } = [];
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public EventCallback<T> OnSelect { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private IReadOnlyList<DialogSelectOption<T>> Items => Options.Select(option => new DialogSelectOption<T>(option.Value,
        option.Title, option.Description, option.Category, SearchText: option.Description, Disabled: option.Disabled)).ToArray();

    private async Task Choose(T value)
    {
        if (Loading || !OnSelect.HasDelegate) return;
        await OnSelect.InvokeAsync(value);
        await OnClose.InvokeAsync();
    }
}
