namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>
    /// Standalone session.skill, not prompt attachment admission. Reusing id repeats the source
    /// durable-event ID and may fail; this call never retries or substitutes an empty prompt.
    /// </summary>
    public Task ActivateSkillAsync(SessionId sessionId, SkillId skill, MessageId? id = null, bool? resume = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(skill.Value);
        if (id is { } supplied) ArgumentNullException.ThrowIfNull(supplied.Value, nameof(id));
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/skill", ct,
            JsonContent.Create(new SessionSkillActivationInput(skill, id, resume), SkillHttpJsonContext.Default.SessionSkillActivationInput));
    }

    /// <summary>Canonical skill.list. A source error is not replaced with an empty catalog.</summary>
    public async Task<LocationResponse<IReadOnlyList<SkillInfo>>> ListSkillsAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/skill" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)), SkillHttpJsonContext.Default.SkillListResult, ct).ConfigureAwait(false);
        if (result.Data is null) throw Malformed("skill.list", "Skill catalog requires a data array.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in result.Data)
        {
            // Skill.ID is a branded string, not necessarily skl_-prefixed. The server owns path
            // semantics; a Windows client must not reject an absolute Unix server skill location.
            if (skill is null || !skill.Id.IsInitialized() || skill.Name is null || skill.Location is null || skill.Content is null || !ids.Add(skill.Id.Value))
                throw Malformed("skill.list", "Skill catalog contains an incomplete or duplicate registered identity.");
        }
        return result;
    }
}

internal sealed record SessionSkillActivationInput(
    [property: JsonPropertyName("skill")] SkillId Skill,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MessageId>))] MessageId? Id,
    [property: JsonPropertyName("resume"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Resume);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<SkillInfo>>), TypeInfoPropertyName = "SkillListResult")]
[JsonSerializable(typeof(SessionSkillActivationInput))]
internal partial class SkillHttpJsonContext : JsonSerializerContext;
