namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PtyIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct PtyId
{
    public const string Prefix = "pty_";
    public static PtyId FromExisting(string value)
    {
        if (value?.StartsWith("pty", StringComparison.Ordinal) != true) throw new JsonException("PTY ID must start with pty.");
        return From(value);
    }
    public static PtyId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => value.StartsWith("pty", StringComparison.Ordinal)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("PTY ID must start with pty.");
    public static PtyId Ascending() => Create();
    public static PtyId Ascending(string id) => FromExisting(id);
    public override string ToString() => Value;
    public static implicit operator string(PtyId id) => id.Value;
    public static explicit operator PtyId(string value) => FromExisting(value);
}

public sealed class PtyIdJsonConverter() : ScalarJsonConverter<PtyId, string>(PtyId.FromExisting, static value => value.Value)
{
    public override PtyId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected PTY ID string.");
        return PtyId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, PtyId value, JsonSerializerOptions options)
    {
        if (!value.IsInitialized() || !value.Value.StartsWith("pty", StringComparison.Ordinal)) throw new JsonException("PTY ID must start with pty.");
        writer.WriteStringValue(value.Value);
    }
}

[JsonConverter(typeof(PtyStatusJsonConverter))]
public enum PtyStatus
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("exited")] Exited
}

public record PtyInfo(PtyId Id, string Title, string Command, IReadOnlyList<string> Args, string Cwd,
    PtyStatus Status, double Pid, double? ExitCode = null)
{
    [JsonPropertyName("id"), JsonRequired]
    public PtyId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Title { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Title);
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Command { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Command);
    [JsonPropertyName("args"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Args { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Args));
    [JsonPropertyName("cwd"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Cwd { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cwd);
    [JsonPropertyName("status"), JsonRequired]
    public PtyStatus Status { get; init => field = PtyStatusJsonConverter.Validate(value); } = PtyStatusJsonConverter.Validate(Status);
    [JsonPropertyName("pid"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Pid { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Pid, 0);
    [JsonPropertyName("exitCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? ExitCode { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); } = ExitCode is null ? null : MessageContract.Integer(ExitCode.Value, 0);
}

public sealed record PtyCreateInput(
    [property: JsonPropertyName("command"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Command = null,
    [property: JsonPropertyName("args"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Cwd = null,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Title = null,
    [property: JsonPropertyName("env"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Env = null
);

public sealed record PtyUpdateInput(
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Title = null,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<TerminalSize>))] TerminalSize? Size = null
);
