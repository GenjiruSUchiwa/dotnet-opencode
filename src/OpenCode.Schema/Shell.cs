namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ShellIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct ShellId
{
    public const string Prefix = "sh_";
    public static ShellId FromExisting(string value)
    {
        if (value?.StartsWith(Prefix, StringComparison.Ordinal) != true) throw new JsonException("Shell ID must start with sh_.");
        return From(value);
    }
    public static ShellId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Shell ID must start with sh_.");
    public static ShellId Ascending() => Create();
    public static ShellId Ascending(string id) => FromExisting(id);
    public override string ToString() => Value;
    public static implicit operator string(ShellId id) => id.Value;
    public static explicit operator ShellId(string value) => FromExisting(value);
}

public sealed class ShellIdJsonConverter() : ScalarJsonConverter<ShellId, string>(ShellId.FromExisting, static value => value.Value)
{
    public override ShellId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected shell ID string.");
        return ShellId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, ShellId value, JsonSerializerOptions options)
    {
        if (!value.IsInitialized() || !value.Value.StartsWith(ShellId.Prefix, StringComparison.Ordinal)) throw new JsonException("Shell ID must start with sh_.");
        writer.WriteStringValue(value.Value);
    }
}

[JsonConverter(typeof(ShellStatusJsonConverter))]
public enum ShellStatus
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("exited")] Exited,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("killed")] Killed
}

public sealed record ShellTime(double Started, double? Completed = null)
{
    [JsonPropertyName("started"), JsonRequired, JsonConverter(typeof(FiniteNumberJsonConverter))]
    public double Started { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Started);
    [JsonPropertyName("completed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFiniteNumberJsonConverter))]
    public double? Completed { get; init => field = value is null ? null : FiniteNumberJsonConverter.Validate(value.Value); } = Completed is null ? null : FiniteNumberJsonConverter.Validate(Completed.Value);
}

public sealed record ShellInfo(
    ShellId Id, ShellStatus Status, string Command, string Cwd, string Shell, string File,
    ShellTime Time, IReadOnlyDictionary<string, JsonElement> Metadata, double? Pid = null, double? Exit = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public ShellId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("status"), JsonRequired]
    public ShellStatus Status { get; init => field = ShellStatusJsonConverter.Validate(value); } = ShellStatusJsonConverter.Validate(Status);
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Command { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Command);
    [JsonPropertyName("cwd"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Cwd { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cwd);
    [JsonPropertyName("shell"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Shell { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Shell);
    [JsonPropertyName("file"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string File { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(File);
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ShellTime>))]
    public ShellTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
    [JsonPropertyName("metadata"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Metadata);
    [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Pid { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); } = Pid is null ? null : MessageContract.Integer(Pid.Value, 0);
    [JsonPropertyName("exit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFiniteNumberJsonConverter))]
    public double? Exit { get; init => field = value is null ? null : FiniteNumberJsonConverter.Validate(value.Value); } = Exit is null ? null : FiniteNumberJsonConverter.Validate(Exit.Value);
}

public sealed record ShellCreateInput(
    string Command, double Timeout,
    [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Cwd = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
)
{
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Command { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Command);
    [JsonPropertyName("timeout"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Timeout { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Timeout, 0);
}

public sealed record ShellOutputInput(double? Cursor = null, double? Limit = null)
{
    [JsonPropertyName("cursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Cursor { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); } = Cursor is null ? null : MessageContract.Integer(Cursor.Value, 0);
    [JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Limit { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); } = Limit is null ? null : MessageContract.Integer(Limit.Value, 0);
}

/// <summary>Shared Shell.Output, also referenced by shell session messages.</summary>
public sealed record ShellOutput(string Output, double Cursor, double Size, [property: JsonPropertyName("truncated"), JsonRequired] bool Truncated)
{
    [JsonPropertyName("output"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Output { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Output);
    [JsonPropertyName("cursor"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Cursor { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Cursor, 0);
    [JsonPropertyName("size"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Size { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Size, 0);
}

/// <summary>Protocol shell.timeout payload.</summary>
public sealed record ShellTimeoutInput(double Timeout)
{
    [JsonPropertyName("timeout"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Timeout { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Timeout, 0);
}
