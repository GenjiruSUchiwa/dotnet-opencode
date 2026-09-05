namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record WorktreeCreatePayload(
    [property: JsonPropertyName("strategy"), JsonRequired, JsonConverter(typeof(WorktreeTrimmedStringConverter))] string Strategy,
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("from"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? From = null,
    [property: JsonPropertyName("branch"), JsonConverter(typeof(WorktreeTrimmedStringConverter))] string? Branch = null,
    [property: JsonPropertyName("name"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null);
public sealed record WorktreeRemovePayload(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("force"), JsonRequired] bool Force);
public sealed record WorktreeErrorData(string Message, bool? ForceRequired = null);
public sealed record WorktreeErrorResponse(string Name, WorktreeErrorData Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WorktreeCreatePayload))]
[JsonSerializable(typeof(WorktreeRemovePayload))]
[JsonSerializable(typeof(WorktreeInfo))]
[JsonSerializable(typeof(IReadOnlyList<WorktreeDirectory>), TypeInfoPropertyName = "WorktreeList")]
[JsonSerializable(typeof(WorktreeErrorResponse))]
public partial class WorktreeProtocolJsonContext : JsonSerializerContext;
