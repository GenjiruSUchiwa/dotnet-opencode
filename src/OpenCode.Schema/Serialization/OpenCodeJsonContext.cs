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
[JsonSerializable(typeof(ModelId))]
[JsonSerializable(typeof(VariantId))]
[JsonSerializable(typeof(ProviderId))]
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(TokenUsageInfo))]
[JsonSerializable(typeof(ModelRef))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(ProviderInfo))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(PromptInput))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(AssistantTextContent))]
[JsonSerializable(typeof(AssistantReasoningContent))]
[JsonSerializable(typeof(AssistantToolContent))]
[JsonSerializable(typeof(OpenCodeConfig))]
[JsonSerializable(typeof(Dictionary<string, AuthEntry>))]
public partial class OpenCodeJsonContext : JsonSerializerContext
{
}
