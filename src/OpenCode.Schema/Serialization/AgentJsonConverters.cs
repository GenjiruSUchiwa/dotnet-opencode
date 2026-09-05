namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class AgentModeJsonConverter : JsonConverter<AgentMode>
{
    internal static AgentMode Validate(AgentMode value) => value is AgentMode.Subagent or AgentMode.Primary or AgentMode.All
        ? value : throw new JsonException("Unknown agent mode.");
    public override AgentMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected agent mode string.");
        return reader.GetString() switch
        {
            "subagent" => AgentMode.Subagent, "primary" => AgentMode.Primary, "all" => AgentMode.All,
            _ => throw new JsonException("Unknown agent mode.")
        };
    }
    public override void Write(Utf8JsonWriter writer, AgentMode value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value) switch
    {
        AgentMode.Subagent => "subagent", AgentMode.Primary => "primary", _ => "all"
    });
}

public sealed class PermissionEffectJsonConverter : JsonConverter<PermissionEffect>
{
    internal static PermissionEffect Validate(PermissionEffect value) => value is PermissionEffect.Allow or PermissionEffect.Deny or PermissionEffect.Ask
        ? value : throw new JsonException("Unknown permission effect.");
    public override PermissionEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected permission effect string.");
        return reader.GetString() switch
        {
            "allow" => PermissionEffect.Allow, "deny" => PermissionEffect.Deny, "ask" => PermissionEffect.Ask,
            _ => throw new JsonException("Unknown permission effect.")
        };
    }
    public override void Write(Utf8JsonWriter writer, PermissionEffect value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value) switch
    {
        PermissionEffect.Allow => "allow", PermissionEffect.Deny => "deny", _ => "ask"
    });
}

public sealed class OptionalPositiveIntegerJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader), 1);
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional step count must be omitted, not null.");
        writer.WriteNumberValue(MessageContract.Integer(value.Value, 1));
    }
}
