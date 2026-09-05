namespace OpenCode.Cli.Tui.Components;

using OpenCode.Schema;

/// <summary>A read-only UI snapshot produced before prompt admission.</summary>
public sealed record PromptConfiguration(
    string? Agent,
    string? Model,
    string? Provider,
    string? Variant,
    string? SelectionError = null,
    string? ExecutionError = null,
    string? Connection = null,
    IReadOnlyList<SessionMessage>? Messages = null,
    string? SessionTitle = null,
    SessionId? SessionId = null,
    ModelRef? ModelSelection = null,
    AgentId? AgentSelection = null,
    bool ChildSession = false,
    ModelRef? AgentModel = null,
    ModelRef? CreationFallback = null,
    string? Directory = null);
