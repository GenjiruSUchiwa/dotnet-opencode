namespace OpenCode.Core.Session.Skills;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionSkillRequest(
    [property: JsonPropertyName("skill"), JsonRequired] SkillId Skill,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MessageId>))] MessageId? Id = null,
    [property: JsonPropertyName("resume"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Resume = null)
    : IJsonOnDeserialized, IJsonOnSerializing
{
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    void IJsonOnSerializing.OnSerializing() => Validate();
    private void Validate()
    {
        if (!Skill.IsInitialized() || (Id is { } id && !id.IsInitialized())) throw new JsonException("Skill activation requires initialized skill/message identifiers.");
    }
}

/// <summary>Exact data for session.skill.activated.1. Text is the registered raw content, not tool-rendered content.</summary>
public sealed record SessionSkillActivatedData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("id"), JsonRequired] SkillId Id,
    [property: JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Name,
    [property: JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Text);

/// <summary>
/// Implement in the existing coordinated Session publication/projector boundary. Append, project,
/// advance sequence and notify after commit. Duplicate supplied event IDs fail; append is not replay
/// or inbox reconciliation. No implementation/SQL fallback is installed by this interface.
/// </summary>
public interface ISessionSkillPublisher
{
    Task PublishAsync(SessionSkillActivatedData data, EventId? eventId, CancellationToken ct);
}

public sealed class SessionSkillNotFoundException(SkillId skill) : Exception($"Skill not found: {skill.Value}")
{
    public SkillId Skill { get; } = skill;
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionSkillActivatedData))]
[JsonSerializable(typeof(SessionSkillRequest))]
public partial class SessionSkillJsonContext : JsonSerializerContext;
