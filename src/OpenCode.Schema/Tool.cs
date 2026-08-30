namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ToolTextContent), "text")]
[JsonDerivedType(typeof(ToolFileContent), "file")]
public abstract record ToolContent;

public sealed record ToolTextContent(
    [property: JsonPropertyName("text")] string Text
) : ToolContent;

public sealed record ToolFileContent(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("name")] string? Name = null
) : ToolContent;

public sealed record ToolContext(
    SessionId SessionId,
    AgentId AgentId,
    MessageId MessageId,
    string CallId,
    Func<IReadOnlyDictionary<string, object>, Task> ReportProgress
);

public sealed record ToolResult(
    string? Output = null,
    IReadOnlyList<ToolContent>? Content = null,
    IReadOnlyDictionary<string, object>? Metadata = null
);
