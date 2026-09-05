namespace OpenCode.Protocol;

using System.Text.Json.Serialization;
using OpenCode.Protocol.Errors;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionCreateInput))]
[JsonSerializable(typeof(SessionPromptInput))]
[JsonSerializable(typeof(ApiResult<SessionInfo>), TypeInfoPropertyName = "SessionResult")]
[JsonSerializable(typeof(ApiPage<SessionInfo>), TypeInfoPropertyName = "SessionsPage")]
[JsonSerializable(typeof(ApiPage<SessionMessage>), TypeInfoPropertyName = "MessagesPage")]
[JsonSerializable(typeof(ApiResult<SessionInboxItem>), TypeInfoPropertyName = "PromptResult")]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<SessionInboxItem>>), TypeInfoPropertyName = "InboxResult")]
[JsonSerializable(typeof(InterruptSessionResponse))]
[JsonSerializable(typeof(SessionViewInput))]
[JsonSerializable(typeof(SessionEnvironmentInput))]
[JsonSerializable(typeof(SessionRenameInput))]
[JsonSerializable(typeof(SessionSwitchAgentInput))]
[JsonSerializable(typeof(SessionSwitchModelInput))]
[JsonSerializable(typeof(ApiResult<IReadOnlyDictionary<string, SessionActive>>), TypeInfoPropertyName = "ActiveSessionsResult")]
[JsonSerializable(typeof(SessionQueryError))]
public partial class SessionProtocolJsonContext : JsonSerializerContext;
