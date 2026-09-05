namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using OpenCode.Cli.Tui.Transcript;
using OpenCode.Schema;
using OpenTui.Blazor.Components;

public partial class OpenCodeApp
{
    private bool _transcriptRowPicker;
    private ImmutableHashSet<string> _expandedRows = [];
    private ImmutableHashSet<string> _collapsedRows = [];

    private IReadOnlyList<SelectOption<string>> TranscriptRowOptions() => TranscriptRows.Create(_transcriptMessages)
        .Where(row => row is ReasoningRow or ExplorationRow or PartRow { Part: AssistantToolContent })
        .Select((row, index) =>
        {
            var title = row switch
            {
                ReasoningRow => "Reasoning",
                ExplorationRow exploration => $"Exploration ({exploration.Parts.Length} tools)",
                PartRow { Part: AssistantToolContent tool } => tool.Name,
                _ => "Details"
            };
            var expanded = !_collapsedRows.Contains(row.Key) && (_expandedRows.Contains(row.Key) ||
                (row is ReasoningRow ? ShowReasoning : ShowToolDetails));
            return new SelectOption<string>(row.Key, $"{index + 1}. {(expanded ? "Collapse" : "Expand")} {title}", row.Key);
        }).ToArray();

    private void ToggleTranscriptRow(string key)
    {
        var row = TranscriptRows.Create(_transcriptMessages).FirstOrDefault(row => row.Key == key);
        if (row is not (ReasoningRow or ExplorationRow or PartRow { Part: AssistantToolContent })) return;
        var expanded = !_collapsedRows.Contains(key) && (_expandedRows.Contains(key) ||
            (row is ReasoningRow ? ShowReasoning : ShowToolDetails));
        _expandedRows = expanded ? _expandedRows.Remove(key) : _expandedRows.Add(key);
        _collapsedRows = expanded ? _collapsedRows.Add(key) : _collapsedRows.Remove(key);
        CloseDialog();
    }
}
