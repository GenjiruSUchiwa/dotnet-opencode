namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(SessionId))]
[JsonSerializable(typeof(ProjectId))]
[JsonSerializable(typeof(MessageId))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(PromptInput))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(UserPromptMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(ToolCallMessage))]
[JsonSerializable(typeof(ToolResultMessage))]
[JsonSerializable(typeof(OpenCodeConfig))]
[JsonSerializable(typeof(Dictionary<string, AuthEntry>))]
public partial class OpenCodeJsonContext : JsonSerializerContext
{
}
