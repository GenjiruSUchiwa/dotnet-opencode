namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record WorktreeCreatePayload(
    [property: JsonPropertyName("strategy"), JsonRequired, JsonConverter(typeof(WorktreeTrimmedStringConverter))] string Strategy,
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? From = null,
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(WorktreeTrimmedStringConverter))] string? Branch = null,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Strategy is null || Directory is null) throw new JsonException("Worktree creation requires strategy and directory.");
    }
}
public sealed record WorktreeRemovePayload(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("force"), JsonRequired] bool Force) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Directory is null) throw new JsonException("Worktree removal requires directory.");
    }
}
public sealed record WorktreeErrorData(
    [property: JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Message,
    [property: JsonPropertyName("forceRequired"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? ForceRequired = null) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Message is null) throw new JsonException("Worktree error requires message.");
    }
}
public sealed record WorktreeErrorResponse(
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("data"), JsonRequired] WorktreeErrorData Data) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        if (Name != "WorktreeError" || Data is null) throw new JsonException("Expected WorktreeError with data.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WorktreeCreatePayload))]
[JsonSerializable(typeof(WorktreeRemovePayload))]
[JsonSerializable(typeof(WorktreeInfo))]
[JsonSerializable(typeof(IReadOnlyList<WorktreeDirectory>), TypeInfoPropertyName = "WorktreeList")]
[JsonSerializable(typeof(WorktreeErrorResponse))]
public partial class WorktreeProtocolJsonContext : JsonSerializerContext;
