namespace OpenCode.Core.Event;

using OpenCode.Schema;

// Recovery-local projections of the shared JobRuntime descriptors. No separate marker writer or job registry.
internal sealed record SubagentRecovery(SessionId ParentSessionId, SessionId ChildSessionId, string Agent, string Description);
internal sealed record SubagentBackground(SessionId Id, MessageId NotificationId, SubagentRecovery Recovery, string Status,
    string? Output = null, string? Error = null);
