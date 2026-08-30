namespace OpenCode.Core.Tools;

using System.Collections.Concurrent;
using System.Text.Json;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Schema;

public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(HttpClient http)
    {
        // Register standard core builtins
        Register(new ReadTool());
        Register(new WriteTool());
        Register(new EditTool());
        Register(new GrepTool());
        Register(new GlobTool());
        Register(new ShellTool());
        Register(new WebFetchTool(http));
    }

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyList<ITool> GetAll() => _tools.Values.ToArray();

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        JsonElement input,
        ToolContext context,
        CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            throw new KeyNotFoundException($"Tool '{toolName}' is not registered.");
        }

        return await tool.ExecuteAsync(input, context, ct);
    }

    /// <summary>
    /// Derives tool definitions for OpenAI-compatible function calling.
    /// </summary>
    public object[] ToOpenAiToolDefinitions()
    {
        return _tools.Values.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            }
        }).ToArray();
    }

    /// <summary>
    /// Derives tool definitions for Google Gemini function calling.
    /// </summary>
    public object ToGeminiToolDeclarations()
    {
        return new
        {
            functionDeclarations = _tools.Values.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            }).ToArray()
        };
    }
}
