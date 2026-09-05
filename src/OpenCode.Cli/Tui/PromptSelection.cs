namespace OpenCode.Cli.Tui;

using OpenCode.Schema;

/// <summary>Client-only immutable admission capture, never additional prompt wire fields.</summary>
public sealed record PromptSelection(LocationRef Location, AgentId? Agent, ModelRef? Model);
