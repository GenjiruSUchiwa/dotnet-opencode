namespace OpenCode.Cli.Tui.Dialogs;

using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenTui.Blazor.Components;

public partial class VariantPicker
{
    [Parameter, EditorRequired] public ModelInfo Model { get; set; } = null!;
    [Parameter] public ModelRef? Current { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public DialogTheme Theme { get; set; } = DialogTheme.Existing;
    [Parameter] public EventCallback<ModelRef> OnSelect { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private IReadOnlyList<PickerOption<ModelRef>> Options() =>
        new[] { new PickerOption<ModelRef>(new(Model.ProviderId.Value, Model.Id.Value), "Default", "No variant override", Disabled: !Model.Enabled) }
            .Concat(Model.Variants.Select(variant => new PickerOption<ModelRef>(
                new(Model.ProviderId.Value, Model.Id.Value, variant.Id.Value), variant.Id.Value,
                $"{Model.ProviderId}/{Model.Id}#{variant.Id}", Disabled: !Model.Enabled))).ToArray();
}
