namespace OpenCode.Core.Tools;

using System.Text.Json;
using OpenCode.Schema;

public sealed record ToolExecutionResult(
    string Content,
    object? Output = null,
    IReadOnlyDictionary<string, object>? Metadata = null
);

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default);
}
