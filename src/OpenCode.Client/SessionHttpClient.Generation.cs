namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Transient text from actual Session context; no prompt admission or automatic retry.</summary>
    public Task<ApiResult<SessionGeneratedText>> GenerateAsync(SessionId sessionId, string prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/generate", SessionGenerationJsonContext.Default.GenerationResult, ct,
            JsonContent.Create(new SessionGenerateHttpInput(prompt), SessionGenerationJsonContext.Default.SessionGenerateHttpInput));
    }
}

public sealed record SessionGeneratedText([property: JsonPropertyName("text"), JsonRequired] string Text);
internal sealed record SessionGenerateHttpInput([property: JsonPropertyName("prompt"), JsonRequired] string Prompt);

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionGenerateHttpInput))]
[JsonSerializable(typeof(ApiResult<SessionGeneratedText>), TypeInfoPropertyName = "GenerationResult")]
internal partial class SessionGenerationJsonContext : JsonSerializerContext;
