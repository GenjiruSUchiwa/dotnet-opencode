namespace OpenCode.Cli.Tui.Transcript;

using System.Collections.Immutable;
using OpenCode.Schema;

public abstract record TranscriptRow(string Key);
public sealed record MessageRow(SessionMessage Message) : TranscriptRow(Message.Id.Value);
public sealed record PartRow(AssistantMessage Message, AssistantContent Part, string PartId)
    : TranscriptRow($"{Message.Id}:{PartId}");
public sealed record ReasoningRow(ImmutableArray<PartRow> Parts, bool Completed)
    : TranscriptRow(Parts[0].Key);
public sealed record ExplorationRow(ImmutableArray<PartRow> Parts, bool Completed)
    : TranscriptRow(Parts[0].Key);
public sealed record AssistantFooterRow(AssistantMessage Message)
    : TranscriptRow($"{Message.Id}:footer");

public static class TranscriptRows
{
    // Input order is canonical history order. Queued inputs remain the caller's responsibility.
    public static ImmutableArray<TranscriptRow> Create(IReadOnlyList<SessionMessage> messages, bool groupExploration = true)
    {
        var rows = new List<TranscriptRow>();
        foreach (var message in messages)
        {
            if (message is not AssistantMessage assistant)
            {
                if (message is SyntheticMessage synthetic && string.IsNullOrWhiteSpace(synthetic.Description)) continue;
                CompletePrevious(rows);
                rows.Add(new MessageRow(message));
                continue;
            }
            var text = 0;
            var reasoning = 0;
            foreach (var part in assistant.Content)
            {
                var id = part switch
                {
                    AssistantToolContent tool => tool.Id,
                    AssistantReasoningContent => $"reasoning:{reasoning++}",
                    _ => $"text:{text++}"
                };
                if (part is AssistantTextContent t && string.IsNullOrWhiteSpace(t.Text)) continue;
                if (part is AssistantReasoningContent r && string.IsNullOrWhiteSpace(r.Text.Replace("[REDACTED]", ""))) continue;
                var row = new PartRow(assistant, part, id);
                if (part is AssistantReasoningContent)
                {
                    if (rows.LastOrDefault() is ReasoningRow previous)
                        rows[^1] = previous with { Parts = previous.Parts.Add(row) };
                    else
                    {
                        CompletePrevious(rows);
                        rows.Add(new ReasoningRow([row], false));
                    }
                    continue;
                }
                if (groupExploration && part is AssistantToolContent exploration && exploration.Name.ToLowerInvariant() is "read" or "glob" or "grep")
                {
                    if (rows.LastOrDefault() is ExplorationRow previous)
                        rows[^1] = previous with { Parts = previous.Parts.Add(row) };
                    else
                    {
                        CompletePrevious(rows);
                        rows.Add(new ExplorationRow([row], false));
                    }
                    continue;
                }
                CompletePrevious(rows);
                rows.Add(row);
            }
            if (assistant.Error is not null || assistant.Retry is not null ||
                assistant.Finish is not null && assistant.Finish is not (LlmFinishReason.ToolCalls or LlmFinishReason.Unknown))
            {
                CompletePrevious(rows);
                rows.Add(new AssistantFooterRow(assistant));
            }
        }
        return rows.ToImmutableArray();
    }

    private static void CompletePrevious(List<TranscriptRow> rows)
    {
        if (rows.LastOrDefault() is ReasoningRow reasoning) rows[^1] = reasoning with { Completed = true };
        if (rows.LastOrDefault() is ExplorationRow exploration) rows[^1] = exploration with { Completed = true };
    }
}
