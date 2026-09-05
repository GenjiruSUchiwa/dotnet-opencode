namespace OpenCode.Schema;

/// <summary>Source-backed definitions for implemented Session data contracts, not the full Session inventory.</summary>
public static class SessionEventDefinitions
{
    public static readonly DurableEventDefinition<SessionCreatedEventData> Created = new(
        "session.created", 1, "sessionID", OpenCodeJsonContext.Default.SessionCreatedEventData);
    public static readonly DurableEventDefinition<SessionInboxEnqueuedEventData> InboxEnqueued = new(
        "session.inbox.enqueued", 1, "sessionID", OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData);
    public static readonly DurableEventDefinition<SessionInboxDeliveredEventData> InboxDelivered = new(
        "session.inbox.delivered", 1, "sessionID", OpenCodeJsonContext.Default.SessionInboxDeliveredEventData);

    public static class Step
    {
        public static readonly DurableEventDefinition<SessionStepStartedEventData> Started = new(
            "session.step.started", 1, "sessionID", OpenCodeJsonContext.Default.SessionStepStartedEventData);
        public static readonly DurableEventDefinition<SessionStepStreamedEventData> Streamed = new(
            "session.step.streamed", 1, "sessionID", OpenCodeJsonContext.Default.SessionStepStreamedEventData);
        public static readonly DurableEventDefinition<SessionStepEndedEventData> Ended = new(
            "session.step.ended", 1, "sessionID", OpenCodeJsonContext.Default.SessionStepEndedEventData);
        public static readonly DurableEventDefinition<SessionStepFailedEventData> Failed = new(
            "session.step.failed", 1, "sessionID", OpenCodeJsonContext.Default.SessionStepFailedEventData);
    }

    public static class Text
    {
        public static readonly DurableEventDefinition<SessionTextStartedEventData> Started = new(
            "session.text.started", 1, "sessionID", OpenCodeJsonContext.Default.SessionTextStartedEventData);
        public static readonly EphemeralEventDefinition<SessionContentDeltaEventData> Delta = new(
            "session.text.delta", OpenCodeJsonContext.Default.SessionContentDeltaEventData);
        public static readonly DurableEventDefinition<SessionContentEndedEventData> Ended = new(
            "session.text.ended", 1, "sessionID", OpenCodeJsonContext.Default.SessionContentEndedEventData);
    }

    public static class Reasoning
    {
        public static readonly DurableEventDefinition<SessionReasoningStartedEventData> Started = new(
            "session.reasoning.started", 1, "sessionID", OpenCodeJsonContext.Default.SessionReasoningStartedEventData);
        public static readonly EphemeralEventDefinition<SessionContentDeltaEventData> Delta = new(
            "session.reasoning.delta", OpenCodeJsonContext.Default.SessionContentDeltaEventData);
        public static readonly DurableEventDefinition<SessionContentEndedEventData> Ended = new(
            "session.reasoning.ended", 1, "sessionID", OpenCodeJsonContext.Default.SessionContentEndedEventData);
    }

    public static class Tool
    {
        public static class Input
        {
            public static readonly DurableEventDefinition<SessionToolInputStartedEventData> Started = new(
                "session.tool.input.started", 1, "sessionID", OpenCodeJsonContext.Default.SessionToolInputStartedEventData);
            public static readonly EphemeralEventDefinition<SessionToolInputDeltaEventData> Delta = new(
                "session.tool.input.delta", OpenCodeJsonContext.Default.SessionToolInputDeltaEventData);
            public static readonly DurableEventDefinition<SessionToolInputEndedEventData> Ended = new(
                "session.tool.input.ended", 1, "sessionID", OpenCodeJsonContext.Default.SessionToolInputEndedEventData);
        }
        public static readonly DurableEventDefinition<SessionToolCalledEventData> Called = new(
            "session.tool.called", 1, "sessionID", OpenCodeJsonContext.Default.SessionToolCalledEventData);
        public static readonly EphemeralEventDefinition<SessionToolProgressEventData> Progress = new(
            "session.tool.progress", OpenCodeJsonContext.Default.SessionToolProgressEventData);
        public static readonly DurableEventDefinition<SessionToolSuccessEventData> Success = new(
            "session.tool.success", 2, "sessionID", OpenCodeJsonContext.Default.SessionToolSuccessEventData);
        public static readonly DurableEventDefinition<SessionToolFailedEventData> Failed = new(
            "session.tool.failed", 2, "sessionID", OpenCodeJsonContext.Default.SessionToolFailedEventData);
    }

    public static readonly DurableEventDefinition<SessionRetryScheduledEventData> RetryScheduled = new(
        "session.retry.scheduled", 1, "sessionID", OpenCodeJsonContext.Default.SessionRetryScheduledEventData);
    public static readonly DurableEventDefinition<SessionMessageContentUpdatedEventData> MessageContentUpdated = new(
        "session.message.content.updated", 1, "sessionID", OpenCodeJsonContext.Default.SessionMessageContentUpdatedEventData);
    public static readonly DurableEventDefinition<SessionUsageRecordedEventData> UsageRecorded = new(
        "session.usage.recorded", 1, "sessionID", OpenCodeJsonContext.Default.SessionUsageRecordedEventData);
    public static readonly EphemeralEventDefinition<SessionUsageUpdatedEventData> UsageUpdated = new(
        "session.usage.updated", OpenCodeJsonContext.Default.SessionUsageUpdatedEventData);

    public static class Compaction
    {
        public static readonly DurableEventDefinition<SessionCompactionStartedEventData> Started = new(
            "session.compaction.started", 1, "sessionID", OpenCodeJsonContext.Default.SessionCompactionStartedEventData);
        public static readonly EphemeralEventDefinition<SessionCompactionDeltaEventData> Delta = new(
            "session.compaction.delta", OpenCodeJsonContext.Default.SessionCompactionDeltaEventData);
        public static readonly DurableEventDefinition<SessionCompactionEndedEventData> Ended = new(
            "session.compaction.ended", 1, "sessionID", OpenCodeJsonContext.Default.SessionCompactionEndedEventData);
        public static readonly DurableEventDefinition<SessionCompactionFailedEventData> Failed = new(
            "session.compaction.failed", 1, "sessionID", OpenCodeJsonContext.Default.SessionCompactionFailedEventData);
    }

    public static IReadOnlyList<EventDefinition> ImplementedDefinitions { get; } = EventDefinitions.Inventory(
        Created, UsageUpdated, InboxDelivered, InboxEnqueued, Step.Started, Step.Streamed, Step.Ended, Step.Failed,
        Text.Started, Text.Delta, Text.Ended, Reasoning.Started, Reasoning.Delta, Reasoning.Ended,
        Tool.Input.Started, Tool.Input.Delta, Tool.Input.Ended, Tool.Called, Tool.Progress, Tool.Success, Tool.Failed,
        RetryScheduled, Compaction.Started, Compaction.Delta, Compaction.Ended, Compaction.Failed, MessageContentUpdated);

    // Upstream deliberately keeps UsageRecorded durable but outside its public Definitions.
    public static IReadOnlyList<EventDefinition> ImplementedDurableDefinitions { get; } = EventDefinitions.Inventory(
        [.. ImplementedDefinitions.Where(definition => definition.Durability == EventDurability.Durable), UsageRecorded]);
}
