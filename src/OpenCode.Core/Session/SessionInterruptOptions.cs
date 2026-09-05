namespace OpenCode.Core.Session;

/// <summary>Continue eligible steering and control work without promoting parked queued prompts.</summary>
public sealed record SessionInterruptOptions(bool Continue = false);
