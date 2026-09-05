namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

/// <summary>integration.get permits an absent data field, not a fabricated integration.</summary>
public sealed record IntegrationGetResponse(
    [property: JsonPropertyName("location"), JsonRequired] LocationInfo Location,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IntegrationInfo>))] IntegrationInfo? Data = null);

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<IntegrationInfo>>> ListIntegrationsAsync(string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/integration" + IntegrationQuery(directory, workspace),
            IntegrationHttpJsonContext.Default.ListResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(item => item is null)) throw Malformed("integration.list", "Integration list requires non-null entries.");
        return result;
    }

    public Task<IntegrationGetResponse> GetIntegrationAsync(IntegrationId integration, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, IntegrationPath(integration) + IntegrationQuery(directory, workspace), IntegrationHttpJsonContext.Default.IntegrationGetResponse, ct);

    public Task ConnectIntegrationKeyAsync(IntegrationId integration, IntegrationKeyConnectPayload input,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, IntegrationPath(integration) + "/connect/key" + IntegrationQuery(directory, workspace), ct,
            JsonContent.Create(input, IntegrationHttpJsonContext.Default.IntegrationKeyConnectPayload));

    public Task<LocationResponse<IntegrationAttempt>> ConnectIntegrationOAuthAsync(IntegrationId integration, IntegrationOAuthConnectPayload input,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, IntegrationPath(integration) + "/connect/oauth" + IntegrationQuery(directory, workspace),
            IntegrationHttpJsonContext.Default.AttemptResult, ct, JsonContent.Create(input, IntegrationHttpJsonContext.Default.IntegrationOAuthConnectPayload));

    public Task<LocationResponse<IntegrationAttemptStatus>> IntegrationOAuthStatusAsync(IntegrationId integration, IntegrationAttemptId attempt,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, IntegrationAttemptPath(integration, "oauth", attempt) + IntegrationQuery(directory, workspace),
            IntegrationHttpJsonContext.Default.StatusResult, ct);

    public Task CompleteIntegrationOAuthAsync(IntegrationId integration, IntegrationAttemptId attempt, string? code = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, IntegrationAttemptPath(integration, "oauth", attempt) + "/complete" + IntegrationQuery(directory, workspace), ct,
            JsonContent.Create(new IntegrationOAuthCompletePayload(code), IntegrationHttpJsonContext.Default.IntegrationOAuthCompletePayload));

    public Task CancelIntegrationOAuthAsync(IntegrationId integration, IntegrationAttemptId attempt,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, IntegrationAttemptPath(integration, "oauth", attempt) + IntegrationQuery(directory, workspace), ct);

    public Task AddWellknownIntegrationAsync(string url, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, "/api/experimental/integration/wellknown" + IntegrationQuery(directory, workspace), ct,
            JsonContent.Create(new IntegrationWellknownAddPayload(url), IntegrationHttpJsonContext.Default.IntegrationWellknownAddPayload));

    public Task<LocationResponse<IntegrationCommandAttempt>> ConnectIntegrationCommandAsync(IntegrationId integration, IntegrationCommandConnectPayload input,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, IntegrationPath(integration) + "/connect/command" + IntegrationQuery(directory, workspace),
            IntegrationHttpJsonContext.Default.CommandResult, ct, JsonContent.Create(input, IntegrationHttpJsonContext.Default.IntegrationCommandConnectPayload));

    public Task<LocationResponse<IntegrationCommandAttemptStatus>> IntegrationCommandStatusAsync(IntegrationId integration, IntegrationAttemptId attempt,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, IntegrationAttemptPath(integration, "command", attempt) + IntegrationQuery(directory, workspace),
            IntegrationHttpJsonContext.Default.CommandStatusResult, ct);

    public Task CancelIntegrationCommandAsync(IntegrationId integration, IntegrationAttemptId attempt,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, IntegrationAttemptPath(integration, "command", attempt) + IntegrationQuery(directory, workspace), ct);

    private static string IntegrationPath(IntegrationId integration)
    {
        ArgumentNullException.ThrowIfNull(integration.Value);
        return "/api/integration/" + Uri.EscapeDataString(integration.Value);
    }

    private static string IntegrationAttemptPath(IntegrationId integration, string method, IntegrationAttemptId attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt.Value);
        return IntegrationPath(integration) + "/connect/" + method + "/" + Uri.EscapeDataString(attempt.Value);
    }

    private static string IntegrationQuery(string? directory, string? workspace) => Query(("location[directory]", directory), ("location[workspace]", workspace));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<IntegrationInfo>>), TypeInfoPropertyName = "ListResult")]
[JsonSerializable(typeof(IntegrationGetResponse))]
[JsonSerializable(typeof(LocationResponse<IntegrationAttempt>), TypeInfoPropertyName = "AttemptResult")]
[JsonSerializable(typeof(LocationResponse<IntegrationAttemptStatus>), TypeInfoPropertyName = "StatusResult")]
[JsonSerializable(typeof(LocationResponse<IntegrationCommandAttempt>), TypeInfoPropertyName = "CommandResult")]
[JsonSerializable(typeof(LocationResponse<IntegrationCommandAttemptStatus>), TypeInfoPropertyName = "CommandStatusResult")]
[JsonSerializable(typeof(IntegrationKeyConnectPayload))]
[JsonSerializable(typeof(IntegrationOAuthConnectPayload))]
[JsonSerializable(typeof(IntegrationOAuthCompletePayload))]
[JsonSerializable(typeof(IntegrationCommandConnectPayload))]
[JsonSerializable(typeof(IntegrationWellknownAddPayload))]
internal partial class IntegrationHttpJsonContext : JsonSerializerContext;
