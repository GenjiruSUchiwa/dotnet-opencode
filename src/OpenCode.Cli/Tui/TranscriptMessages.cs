namespace OpenCode.Cli.Tui;

using System.Collections.Immutable;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>Normalizes observed facts into render models without publishing synthetic events.</summary>
internal static class TranscriptMessages
{
    internal static IReadOnlyList<SessionMessage> Build(IReadOnlyList<SessionMessage> history,
        IReadOnlyList<SessionResponseSnapshot> responses, IReadOnlyDictionary<MessageId, string> prompts)
    {
        var messages = history.ToList();
        var indexes = messages.Select((message, index) => (message.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        foreach (var response in responses)
        {
            if (response.Delivered && response.DeliveredAt is { } delivered && prompts.TryGetValue(response.PromptId, out var prompt)
                && !indexes.ContainsKey(response.PromptId))
                Put(new UserMessage { Id = response.PromptId, Time = new(delivered), Text = prompt });
            foreach (var step in response.Steps)
            {
                var original = step.ReadModel;
                var created = step.Created ?? original?.Time.Created;
                var agent = step.Started?.Agent ?? original?.Agent;
                var model = step.Started?.Model ?? original?.Model;
                if (created is null || agent is null || model is null) continue;
                var content = new List<(int Order, AssistantContent Content)>();
                foreach (var part in response.Content.Where(part => part.AssistantId == step.Id))
                {
                    if (part.Kind == "text") content.Add((part.Order, new AssistantTextContent(part.Text, part.State)));
                    if (part.Kind == "reasoning") content.Add((part.Order, new AssistantReasoningContent(part.Text, part.State,
                        part.Created is { } start ? new MessageTime(start, part.Completed) : null)));
                }
                foreach (var tool in response.Tools.Where(tool => tool.AssistantId == step.Id))
                {
                    if (tool.Created is not { } start) continue;
                    ToolState? state = tool.Status switch
                    {
                        "streaming" => new ToolStateStreaming(tool.Input),
                        "running" when tool.Arguments is not null => new ToolStateRunning(tool.Arguments,
                            tool.Metadata ?? ImmutableDictionary<string, JsonElement>.Empty),
                        "completed" when tool.Arguments is not null && tool.Output is { Count: > 0 } => new ToolStateCompleted(tool.Arguments, tool.Output, tool.Metadata),
                        "error" when tool.Error is not null => new ToolStateError(tool.Arguments ?? ImmutableDictionary<string, JsonElement>.Empty,
                            tool.Error, tool.Output, tool.Metadata),
                        _ => null
                    };
                    if (state is not null) content.Add((tool.Order, new AssistantToolContent(tool.Id, tool.Name, state,
                        new AssistantToolTime(start, tool.Ran, tool.Completed), tool.Executed, tool.ProviderState, tool.ProviderResultState)));
                }
                Put(new AssistantMessage
                {
                    Id = step.Id,
                    Agent = agent,
                    Model = model,
                    Time = new(created.Value, step.Completed ?? original?.Time.Completed, step.StreamedAt ?? original?.Time.Streamed),
                    Content = content.Count > 0 ? content.OrderBy(part => part.Order).Select(part => part.Content).ToArray() : original?.Content ?? [],
                    Tokens = step.Ended?.Tokens ?? step.Failed?.Tokens ?? original?.Tokens,
                    Cost = step.Ended?.Cost ?? step.Failed?.Cost ?? original?.Cost,
                    Error = step.Failed?.Error ?? original?.Error,
                    Finish = step.Ended?.Finish ?? (step.Failed?.Finish == "content-filter" ? LlmFinishReason.ContentFilter : original?.Finish),
                    RawFinish = step.Ended?.RawFinish ?? step.Failed?.RawFinish ?? original?.RawFinish,
                    ProviderState = step.Ended?.ProviderState ?? step.Failed?.ProviderState ?? original?.ProviderState,
                    Metadata = original?.Metadata,
                    Retry = original?.Retry,
                    Snapshot = original?.Snapshot
                });
            }
        }
        return messages;

        void Put(SessionMessage message)
        {
            if (indexes.TryGetValue(message.Id, out var index)) messages[index] = message;
            else { indexes.Add(message.Id, messages.Count); messages.Add(message); }
        }
    }
}
