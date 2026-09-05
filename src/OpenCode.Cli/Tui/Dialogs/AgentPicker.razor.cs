namespace OpenCode.Cli.Tui.Dialogs;

using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenTui.Blazor.Components;

public partial class AgentPicker
{
    [Parameter] public IReadOnlyList<AgentInfo> Agents { get; set; } = [];
    [Parameter] public AgentId? Current { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public DialogTheme Theme { get; set; } = DialogTheme.Existing;
    [Parameter] public EventCallback<AgentId> OnSelect { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private IReadOnlyList<PickerOption<AgentId?>> Options() => Agents
        .Where(agent => !agent.Hidden && agent.Mode != AgentMode.Subagent)
        .Select(agent => new PickerOption<AgentId?>(agent.Id, agent.Name, agent.Description)).ToArray();

    private Task SelectAsync(AgentId? value) => value is { } id ? OnSelect.InvokeAsync(id) : Task.CompletedTask;
}
