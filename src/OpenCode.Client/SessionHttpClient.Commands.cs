namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<CommandInfo>>> ListCommandsAsync(string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/command" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)), CommandHttpJsonContext.Default.CommandsResult, ct);
        if (result.Data.Any(command => command is null || command.Name is null))
            throw Malformed("command.list", "The command catalog contains a missing command identity.");
        return result;
    }

    public Task ExecuteCommandAsync(SessionId sessionId, string command, PromptInput prompt,
        InboxDeliveryMode? delivery = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(prompt);
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/command", ct,
            JsonContent.Create(new CommandHttpInput(command, prompt.Text, prompt.Files, prompt.Agents, prompt.Skills, delivery),
                CommandHttpJsonContext.Default.CommandHttpInput));
    }
}

internal sealed record CommandHttpInput(
    [property: JsonPropertyName("command"), JsonRequired] string Command,
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptInputFileAttachment>? Files,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptInputSkillAttachment>? Skills,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode? Delivery);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<CommandInfo>>), TypeInfoPropertyName = "CommandsResult")]
[JsonSerializable(typeof(CommandHttpInput))]
internal partial class CommandHttpJsonContext : JsonSerializerContext;
