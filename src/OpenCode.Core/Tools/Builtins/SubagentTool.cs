namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;
using System.Collections.Immutable;
using OpenCode.Core.Agent;
using OpenCode.Core.Llm;
using OpenCode.Core.Permissions;
using OpenCode.Core.Session.Subagents;
using OpenCode.Schema;

/// <summary>Location permission leaf over the host's shared child-job service.</summary>
public sealed class SubagentTool(SessionSubagents subagents, PermissionService permissions)
{
    public const string Name = "subagent";
    public ToolInfo Create() => ToolInfo.FromJson(Name,
        "Spawns an agent in a child session to work on the specified task.\nThe output includes a sessionID you can pass back later to continue that specific conversation with the subagent.\nNew child sessions start with fresh context, so include all relevant context and instructions when you don't pass a sessionID.\nForeground (default) runs the subagent to completion and returns its final response.\nBackground mode (background=true) launches it asynchronously and returns immediately; you are notified when it finishes.\nUse background only for independent work that can run while you continue elsewhere.",
        JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"agent":{"type":"string","description":"The type of specialized agent to use for this task"},"description":{"type":"string","description":"A short 3-5 word label for the task, displayed to the user"},"prompt":{"type":"string","description":"The task for the subagent to perform"},"sessionID":{"type":"string","pattern":"^ses","description":"Continue a specific previous subagent conversation. Omit to start a new conversation."},"background":{"type":"boolean","description":"Run in the background. You will be notified on completion; do not poll."}},"required":["agent","description","prompt"]}
            """), ExecuteAsync, JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"sessionID":{"type":"string","pattern":"^ses"},"status":{"type":"string","enum":["completed","running"]},"output":{"type":"string"}},"required":["sessionID","status","output"]}
            """), new ToolOptions(CodeMode: false));

    private async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var result = await subagents.RunAsync(input.GetProperty("agent").GetString()!, input.GetProperty("description").GetString()!, input.GetProperty("prompt").GetString()!,
            input.TryGetProperty("sessionID", out var session) ? SessionId.FromExisting(session.GetString()!) : null,
            input.TryGetProperty("background", out var background) && background.GetBoolean(), context, permissions, ct).ConfigureAwait(true);
        return new(result.Status == "completed" ? $"<subagent sessionID=\"{result.SessionId}\" state=\"completed\">\n{result.Output}\n</subagent>" : result.Output,
            new { sessionID = result.SessionId.Value, status = result.Status, output = result.Output },
            new Dictionary<string, object> { ["sessionID"] = result.SessionId.Value, ["status"] = result.Status });
    }

    /// <summary>The source builtin context hook decorates only the captured description; execution authority is unchanged.</summary>
    internal static async Task<ImmutableArray<LlmToolDefinition>> PrepareDefinitionsAsync(IReadOnlyList<ToolDefinition> definitions,
        string directory, AgentInfo selected, CancellationToken ct)
    {
        var suffix = "";
        if (definitions.Any(definition => definition.Name == Name))
        {
            var available = (await AgentCatalog.ListAsync(directory, ct).ConfigureAwait(true)).Where(agent => agent.Mode != AgentMode.Primary && !agent.Hidden &&
                PermissionRules.Evaluate(Name, agent.Id.Value, selected.Permissions).Effect != PermissionEffect.Deny)
                .OrderBy(agent => agent.Id.Value, StringComparer.CurrentCulture).ToArray();
            if (available.Length > 0)
                suffix = "\n\nAvailable subagents:\n" + string.Join('\n', available.Select(agent =>
                    $"- {agent.Id}: {agent.Description ?? "This subagent should only be called when explicitly requested."}"));
        }
        return definitions.Select(definition => new LlmToolDefinition(definition.Name,
            definition.Description + (definition.Name == Name ? suffix : ""), definition.InputSchema)).ToImmutableArray();
    }
}
