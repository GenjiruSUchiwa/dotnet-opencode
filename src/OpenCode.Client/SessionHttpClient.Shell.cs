namespace OpenCode.Client;

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<ShellInfo>>> ListShellsAsync(string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/shell" + ShellQuery(directory, workspace), ShellHttpJsonContext.Default.ListResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(info => info is null)) throw Malformed("shell.list", "Shell list must contain non-null command records.");
        return result;
    }

    public Task<LocationResponse<ShellInfo>> CreateShellAsync(ShellCreateInput input, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, "/api/shell" + ShellQuery(directory, workspace), ShellHttpJsonContext.Default.InfoResult, ct,
            JsonContent.Create(input, ShellHttpJsonContext.Default.ShellCreateInput));

    public Task<LocationResponse<ShellInfo>> GetShellAsync(ShellId id, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, ShellPath(id) + ShellQuery(directory, workspace), ShellHttpJsonContext.Default.InfoResult, ct);

    public Task<LocationResponse<ShellInfo>> SetShellTimeoutAsync(ShellId id, double milliseconds, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Patch, ShellPath(id) + "/timeout" + ShellQuery(directory, workspace), ShellHttpJsonContext.Default.InfoResult, ct,
            JsonContent.Create(new ShellTimeoutInput(milliseconds), ShellHttpJsonContext.Default.ShellTimeoutInput));

    public Task<LocationResponse<ShellOutput>> ReadShellOutputAsync(ShellId id, ShellOutputInput? input = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, ShellPath(id) + "/output" + Query(("location[directory]", directory), ("location[workspace]", workspace),
            ("cursor", input?.Cursor?.ToString("R", CultureInfo.InvariantCulture)), ("limit", input?.Limit?.ToString("R", CultureInfo.InvariantCulture))),
            ShellHttpJsonContext.Default.OutputResult, ct);

    /// <summary>Canonical interrupt/removal operation. Removes the retained command and output too.</summary>
    public Task RemoveShellAsync(ShellId id, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, ShellPath(id) + ShellQuery(directory, workspace), ct);

    /// <summary>Explicit user shell command. HTTP cancellation cancels the wait, not server-owned Session completion.</summary>
    public Task RunSessionShellAsync(SessionId session, string command, EventId? id = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return NoContentAsync(HttpMethod.Post, SessionPath(session) + "/shell", ct,
            JsonContent.Create(new SessionShellInput(command, id), ShellHttpJsonContext.Default.SessionShellInput));
    }

    private static string ShellPath(ShellId id)
    {
        if (!id.IsInitialized() || !id.Value.StartsWith(ShellId.Prefix, StringComparison.Ordinal)) throw new ArgumentException("A canonical sh_ identifier is required.", nameof(id));
        return "/api/shell/" + Uri.EscapeDataString(id.Value);
    }
    private static string ShellQuery(string? directory, string? workspace) => Query(("location[directory]", directory), ("location[workspace]", workspace));
}

internal sealed record SessionShellInput([property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<EventId>))] EventId? Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<ShellInfo>>), TypeInfoPropertyName = "ListResult")]
[JsonSerializable(typeof(LocationResponse<ShellInfo>), TypeInfoPropertyName = "InfoResult")]
[JsonSerializable(typeof(LocationResponse<ShellOutput>), TypeInfoPropertyName = "OutputResult")]
[JsonSerializable(typeof(ShellCreateInput))]
[JsonSerializable(typeof(ShellTimeoutInput))]
[JsonSerializable(typeof(SessionShellInput))]
internal partial class ShellHttpJsonContext : JsonSerializerContext;
