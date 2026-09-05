namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task<ApiResult<IReadOnlyList<InstructionEntryInfo>>> ListInstructionEntriesAsync(SessionId sessionId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/instructions/entries", InstructionHttpJsonContext.Default.EntriesResult, ct);

    public Task PutInstructionEntryAsync(SessionId sessionId, string key, JsonElement value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return NoContentAsync(HttpMethod.Put, SessionPath(sessionId) + "/instructions/entries/" + Uri.EscapeDataString(key), ct,
            JsonContent.Create(new InstructionEntryHttpInput(value), InstructionHttpJsonContext.Default.InstructionEntryHttpInput));
    }

    public Task RemoveInstructionEntryAsync(SessionId sessionId, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return NoContentAsync(HttpMethod.Delete, SessionPath(sessionId) + "/instructions/entries/" + Uri.EscapeDataString(key), ct);
    }
}

internal sealed record InstructionEntryHttpInput([property: JsonPropertyName("value"), JsonRequired] JsonElement Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<InstructionEntryInfo>>), TypeInfoPropertyName = "EntriesResult")]
[JsonSerializable(typeof(InstructionEntryHttpInput))]
internal partial class InstructionHttpJsonContext : JsonSerializerContext;
