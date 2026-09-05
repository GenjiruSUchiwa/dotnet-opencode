namespace OpenCode.Core.Instructions;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Core.Mcp;
using OpenCode.Core.Permissions;
using OpenCode.Core.Tools;
using OpenCode.Schema;

internal static class McpInstructionSource
{
    internal static McpConfiguration Configuration(string directory, JsonObject document)
    {
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var sources = ProducerConfiguration.Read(directory, home, Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory()), document);
        sources.RequireNoPluginSources();
        return McpRuntime.Configure(sources.Documents.Where(source => source.Info.ContainsKey("mcp"))
            .Select(source => source.Info["mcp"]?.Deserialize(OpenCodeJsonContext.Default.McpConfiguration)
                ?? throw new JsonException("MCP configuration must be an object.")));
    }

    internal static InstructionSource FromObservation(McpObservation observation, AgentInfo agent)
    {
        var canExecute = PermissionRules.Evaluate("execute", "*", agent.Permissions).Effect != PermissionEffect.Deny;
        var summaries = observation.Guidance.SelectMany(guidance =>
        {
            var owned = observation.Tools.Where(tool => tool.Server == guidance.Server).ToArray();
            var codeMode = owned.FirstOrDefault()?.CodeMode != false;
            if (codeMode && !canExecute || !owned.Any(tool => PermissionRules.Evaluate(
                    ToolInfo.EffectiveName(tool.Tool.Name, ToolInfo.NormalizedName(tool.Server)), "*", agent.Permissions).Effect != PermissionEffect.Deny))
                return Array.Empty<JsonObject>();
            var summary = new JsonObject { ["server"] = guidance.Server, ["instructions"] = guidance.Instructions };
            if (!codeMode) summary["codemode"] = false;
            return new[] { summary };
        }).OrderBy(summary => summary["server"]!.GetValue<string>(), StringComparer.CurrentCulture).ToArray();
        return Create(summaries.Length == 0 ? InstructionAvailability.Removed : InstructionAvailability.Available,
            JsonSerializer.SerializeToElement(summaries));
    }

    internal static InstructionSource WithoutRuntime(ProducerConfiguration configuration)
    {
        if (!configuration.ReferencesAvailable) return Create(InstructionAvailability.Unavailable);
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var document in configuration.Documents)
        {
            if (!document.Info.TryGetPropertyValue("mcp", out var value)) continue;
            var config = value?.Deserialize(OpenCodeJsonContext.Default.McpConfiguration)
                ?? throw new JsonException("MCP configuration must be an object.");
            // ConfigMcpPlugin replaces each server in document order, rather than merging its fields.
            if (config.Servers is not null)
                foreach (var server in config.Servers) servers[server.Key] = server.Value;
        }
        return Create(servers.Values.All(server => server is McpLocalConfig { Disabled: true } or McpRemoteConfig { Disabled: true })
            ? InstructionAvailability.Removed : InstructionAvailability.Unavailable);
    }

    // The MCP producer supplies the settled, permission-visible, server-sorted Summary array.
    // Unavailable means the observation itself failed, not that an individual server failed to connect.
    internal static InstructionSource Create(InstructionAvailability availability, JsonElement value = default) =>
        new("core/mcp-guidance", availability, value, Render, Update, _ => "MCP server instructions are no longer available.");

    private static string Render(JsonElement servers) => string.Join("\n",
        new[] { "<mcp_instructions>" }.Concat(Entries(servers.EnumerateArray())).Append("</mcp_instructions>"));

    private static IEnumerable<string> Entries(IEnumerable<JsonElement> servers) => servers.SelectMany(server =>
    {
        var name = server.GetProperty("server").GetString()!;
        var lines = new List<string> { $"  <server name=\"{name}\">" };
        if (!server.TryGetProperty("codemode", out var mode) || mode.ValueKind != JsonValueKind.False)
            lines.Add("    Use tools from this server through `execute` under `tools[" +
                InstructionJson.Stringify(JsonSerializer.SerializeToElement(Regex.Replace(name, "[^a-zA-Z0-9_-]", "_", RegexOptions.NonBacktracking))) + "]`.");
        lines.AddRange(server.GetProperty("instructions").GetString()!.Split('\n').Select(line => "    " + line));
        lines.Add("  </server>");
        return lines;
    });

    private static string Update(JsonElement previous, JsonElement current)
    {
        var before = previous.EnumerateArray().ToDictionary(server => server.GetProperty("server").GetString()!, StringComparer.Ordinal);
        var after = current.EnumerateArray().ToDictionary(server => server.GetProperty("server").GetString()!, StringComparer.Ordinal);
        var added = after.Where(server => !before.ContainsKey(server.Key)).Select(server => server.Value).ToArray();
        var removed = before.Keys.Where(name => !after.ContainsKey(name)).ToArray();
        var changed = after.Any(server => before.TryGetValue(server.Key, out var old) &&
            (old.GetProperty("instructions").GetString() != server.Value.GetProperty("instructions").GetString() ||
             old.TryGetProperty("codemode", out _) != server.Value.TryGetProperty("codemode", out _)));
        if (changed || added.Length == 0 && removed.Length == 0)
            return "The available MCP server instructions have changed. This list supersedes the previous one.\n" + Render(current);
        var lines = new List<string>();
        if (added.Length > 0)
        {
            lines.Add("New MCP server instructions are available in addition to those previously listed:");
            lines.AddRange(Entries(added));
        }
        if (removed.Length > 0)
            lines.Add("Instructions for the following MCP servers are no longer available: " + string.Join(", ", removed) + ".");
        return string.Join("\n", lines);
    }
}
