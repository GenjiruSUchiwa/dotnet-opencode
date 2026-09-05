namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record VcsBase(string Name, string Ref, string Source)
{
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("ref"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Ref { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Ref);
    [JsonPropertyName("source"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Source { get; init => field = ValidateSource(value); } = ValidateSource(Source);
    private static string ValidateSource(string value) => value is "reflog" or "default" ? value : throw new JsonException("VCS base source must be reflog or default.");
}

[JsonConverter(typeof(VcsDiffModeJsonConverter))]
public enum VcsDiffMode { Working, Branch, Committed }

public sealed class VcsDiffModeJsonConverter : JsonConverter<VcsDiffMode>
{
    public static string Encode(VcsDiffMode value) => value switch
    {
        VcsDiffMode.Working => "working", VcsDiffMode.Branch => "branch", VcsDiffMode.Committed => "committed",
        _ => throw new JsonException("Unknown VCS diff mode.")
    };
    public override VcsDiffMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "working" => VcsDiffMode.Working, "branch" => VcsDiffMode.Branch, "committed" => VcsDiffMode.Committed,
            _ => throw new JsonException("VCS mode must be working, branch, or committed.")
        } : throw new JsonException("VCS mode must be a string.");
    public override void Write(Utf8JsonWriter writer, VcsDiffMode value, JsonSerializerOptions options) => writer.WriteStringValue(Encode(value));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VcsBase))]
[JsonSerializable(typeof(VcsDiffMode))]
public partial class VcsJsonContext : JsonSerializerContext;
