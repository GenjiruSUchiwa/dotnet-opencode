namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class OptionalNonNegativeIntegerJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader), 0);
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional integer must be omitted, not null.");
        writer.WriteNumberValue(MessageContract.Integer(value.Value, 0));
    }
}

public sealed class ProjectVcsJsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    internal static string Validate(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] is < 'a' or > 'z') throw new JsonException("Invalid project VCS identifier.");
        foreach (var character in value.AsSpan(1))
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not ('.' or '_' or '-'))
                throw new JsonException("Invalid project VCS identifier.");
        return value;
    }
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected project VCS string.");
        return Validate(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value));
}

public sealed class ProjectIdListJsonConverter : JsonConverter<IReadOnlyList<ProjectId>>
{
    public override bool HandleNull => true;
    public override IReadOnlyList<ProjectId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected project ID array.");
        var result = new List<ProjectId>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return result;
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Project IDs must be strings.");
            result.Add(ProjectId.FromExisting(reader.GetString()!));
        }
        throw new JsonException("Unterminated project ID array.");
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ProjectId> value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value);
        writer.WriteStartArray();
        foreach (var id in value) writer.WriteStringValue(PromptValidation.Required(id.Value));
        writer.WriteEndArray();
    }
}
