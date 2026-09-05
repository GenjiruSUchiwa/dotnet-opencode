namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class OptionalFiniteNumberJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => FiniteNumberJsonConverter.ReadValue(ref reader);
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional finite number must be omitted, not null.");
        FiniteNumberJsonConverter.WriteValue(writer, value.Value);
    }
}

public sealed class SchemaNumberJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => FormNumberJson.Read(ref reader);
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => FormNumberJson.Write(writer, value);
}

public sealed class ShellStatusJsonConverter : JsonConverter<ShellStatus>
{
    internal static ShellStatus Validate(ShellStatus value) => value is ShellStatus.Running or ShellStatus.Exited or ShellStatus.Timeout or ShellStatus.Killed
        ? value : throw new JsonException("Unknown shell status.");
    public override ShellStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected shell status string.");
        return reader.GetString() switch
        {
            "running" => ShellStatus.Running, "exited" => ShellStatus.Exited, "timeout" => ShellStatus.Timeout, "killed" => ShellStatus.Killed,
            _ => throw new JsonException("Unknown shell status.")
        };
    }
    public override void Write(Utf8JsonWriter writer, ShellStatus value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value) switch
    {
        ShellStatus.Running => "running", ShellStatus.Exited => "exited", ShellStatus.Timeout => "timeout", _ => "killed"
    });
}

public sealed class PtyStatusJsonConverter : JsonConverter<PtyStatus>
{
    internal static PtyStatus Validate(PtyStatus value) => value is PtyStatus.Running or PtyStatus.Exited ? value : throw new JsonException("Unknown PTY status.");
    public override PtyStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected PTY status string.");
        return reader.GetString() switch
        {
            "running" => PtyStatus.Running, "exited" => PtyStatus.Exited,
            _ => throw new JsonException("Unknown PTY status.")
        };
    }
    public override void Write(Utf8JsonWriter writer, PtyStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Validate(value) == PtyStatus.Running ? "running" : "exited");
}

public sealed class PersistentPtyReadLinesJsonConverter() : ScalarJsonConverter<PersistentPtyReadLines, double>(PersistentPtyReadLines.FromExisting, static value => value.Value)
{
    public override PersistentPtyReadLines Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PersistentPtyReadLines.FromExisting(FiniteNumberJsonConverter.ReadValue(ref reader));
    public override void Write(Utf8JsonWriter writer, PersistentPtyReadLines value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(PersistentPtyReadLines.Require(value.Value));
}

public sealed class PtyCheckpointJsonConverter : JsonConverter<byte[]>
{
    public override bool HandleNull => true;
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("PTY checkpoint must be a base64 string.");
        // Effect's Uint8Array codec strips CR/LF, but does not accept other base64 whitespace.
        var value = reader.GetString()!.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        if (!PromptBase64.IsValid(value.AsSpan())) throw new JsonException("Invalid checkpoint base64.");
        return Convert.FromBase64String(value);
    }
    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(PromptValidation.Required(value));
}
