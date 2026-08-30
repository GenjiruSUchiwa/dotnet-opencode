namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<LlmFinishReason>))]
public enum LlmFinishReason
{
    [JsonStringEnumMemberName("stop")]
    Stop,
    [JsonStringEnumMemberName("length")]
    Length,
    [JsonStringEnumMemberName("tool-calls")]
    ToolCalls,
    [JsonStringEnumMemberName("content-filter")]
    ContentFilter,
    [JsonStringEnumMemberName("error")]
    Error,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}
