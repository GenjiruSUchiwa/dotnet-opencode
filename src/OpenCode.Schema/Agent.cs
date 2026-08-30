namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(string Value) : IEquatable<AgentId>
{
    public override string ToString() => Value;
    public static implicit operator string(AgentId id) => id.Value;
    public static explicit operator AgentId(string value) => new(value);
}

public sealed class AgentIdJsonConverter : JsonConverter<AgentId>
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentMode>))]
public enum AgentMode
{
    [JsonStringEnumMemberName("subagent")]
    Subagent,
    [JsonStringEnumMemberName("primary")]
    Primary,
    [JsonStringEnumMemberName("all")]
    All
}

public sealed record ProviderRequest(
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, object>? Settings = null,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body")] IReadOnlyDictionary<string, object>? Body = null
);

/// <summary>
/// 1:1 port of Agent.Info from packages/schema/src/agent.ts
/// </summary>
public sealed record AgentInfo(
    [property: JsonPropertyName("id")] AgentId Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mode")] AgentMode Mode,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("permissions")] IReadOnlyList<PermissionRule> Permissions,
    [property: JsonPropertyName("request")] ProviderRequest? Request = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null,
    [property: JsonPropertyName("system")] string? System = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("steps")] int? Steps = null
)
{
    public static AgentInfo CreateDefault(AgentId id) => new(
        Id: id,
        Name: id.Value,
        Mode: AgentMode.Primary,
        Hidden: false,
        Permissions: [
            new PermissionRule("*", "*", PermissionEffect.Allow),
            new PermissionRule("external_directory", "*", PermissionEffect.Ask),
            new PermissionRule("read", "*.env", PermissionEffect.Ask),
            new PermissionRule("read", "*.env.*", PermissionEffect.Ask),
            new PermissionRule("read", "*.env.example", PermissionEffect.Allow)
        ]
    );
}
