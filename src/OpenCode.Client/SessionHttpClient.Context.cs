namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<ApiResult<SessionMessage>> MessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId.Value);
        var result = await RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/message/" + Uri.EscapeDataString(messageId.Value),
            SessionContextHttpJsonContext.Default.MessageResult, ct).ConfigureAwait(false);
        if (result.Data.Id != messageId) throw Malformed("session.message", "The returned message does not match the requested identity.");
        return result;
    }

    public Task<ApiResult<IReadOnlyList<SessionMessage>>> ContextAsync(SessionId sessionId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/context", SessionContextHttpJsonContext.Default.ContextResult, ct);

    public Task<ApiResult<SessionRevert>> StageRevertAsync(SessionId sessionId, MessageId messageId, bool? files = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId.Value);
        return RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/revert/stage", SessionContextHttpJsonContext.Default.RevertResult, ct,
            JsonContent.Create(new StageRevertHttpInput(messageId, files), SessionContextHttpJsonContext.Default.StageRevertHttpInput));
    }

    public Task ClearRevertAsync(SessionId sessionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/revert/clear", ct);

    public Task CommitRevertAsync(SessionId sessionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/revert/commit", ct);
}

internal sealed record StageRevertHttpInput([property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId,
    [property: JsonPropertyName("files")] bool? Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(ApiResult<SessionMessage>), TypeInfoPropertyName = "MessageResult")]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<SessionMessage>>), TypeInfoPropertyName = "ContextResult")]
[JsonSerializable(typeof(ApiResult<SessionRevert>), TypeInfoPropertyName = "RevertResult")]
[JsonSerializable(typeof(StageRevertHttpInput))]
internal partial class SessionContextHttpJsonContext : JsonSerializerContext;
