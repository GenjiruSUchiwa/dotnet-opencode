namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record TerminalSize(double Cols, double Rows)
{
    [JsonPropertyName("cols"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Cols { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Cols, 1);
    [JsonPropertyName("rows"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Rows { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Rows, 1);
}

public sealed record TerminalOutputOffsets(double Head, double Tail)
{
    [JsonPropertyName("head"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Head { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Head, 0);
    [JsonPropertyName("tail"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Tail { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Tail, 0);
}

public sealed record TerminalCursor(double X, double Y)
{
    [JsonPropertyName("x"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double X { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(X, 0);
    [JsonPropertyName("y"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Y { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Y, 0);
}

public sealed record PersistentPtyInfo(
    PtyId Id, SessionId SessionId, string Title, string Command, IReadOnlyList<string> Args, string Cwd,
    PtyStatus Status, double Pid, TerminalSize Size, TerminalOutputOffsets Output,
    [property: JsonPropertyName("foregroundProcess"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ForegroundProcess,
    double? ExitCode = null
) : PtyInfo(Id, Title, Command, Args, Cwd, Status, Pid, ExitCode)
{
    [JsonPropertyName("sessionID"), JsonRequired]
    public SessionId SessionId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(SessionId, SessionId.IsInitialized());
    [JsonPropertyName("size"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalSize>))]
    public TerminalSize Size { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Size);
    [JsonPropertyName("output"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalOutputOffsets>))]
    public TerminalOutputOffsets Output { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Output);
}

public sealed record PersistentPtyHandoff(string Directory, string InstanceId, string Ticket,
    [property: JsonPropertyName("expiresAt"), JsonRequired, JsonConverter(typeof(SchemaNumberJsonConverter))] double ExpiresAt)
{
    [JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Directory { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Directory);
    [JsonPropertyName("instanceID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string InstanceId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(InstanceId);
    [JsonPropertyName("ticket"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Ticket { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Ticket);
}

public sealed record PersistentPtyCreateInput(
    IReadOnlyList<string> Args, string Title, IReadOnlyDictionary<string, string> Env,
    [property: JsonPropertyName("command"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Command = null,
    [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Cwd = null,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<TerminalSize>))] TerminalSize? Size = null
)
{
    [JsonPropertyName("args"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Args { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Args));
    [JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Title { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Title);
    [JsonPropertyName("env"), JsonRequired, JsonConverter(typeof(ProviderHeadersJsonConverter))]
    public IReadOnlyDictionary<string, string> Env { get; init => field = ProviderHeadersJsonConverter.Validate(value); } = ProviderHeadersJsonConverter.Validate(Env);
}

public sealed record PersistentPtyUpdateInput(
    TerminalSize Size,
    [property: JsonPropertyName("attachmentID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? AttachmentId = null
)
{
    [JsonPropertyName("size"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalSize>))]
    public TerminalSize Size { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Size);
}

public sealed record PersistentPtySnapshot(PersistentPtyInfo Info, string Text, byte[] Checkpoint, TerminalCursor Cursor)
{
    [JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<PersistentPtyInfo>))]
    public PersistentPtyInfo Info { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Info);
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
    [JsonPropertyName("checkpoint"), JsonRequired, JsonConverter(typeof(PtyCheckpointJsonConverter))]
    public byte[] Checkpoint { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Checkpoint);
    [JsonPropertyName("cursor"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalCursor>))]
    public TerminalCursor Cursor { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cursor);
}

[JsonConverter(typeof(PersistentPtyReadLinesJsonConverter))]
[Vogen.ValueObject<double>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct PersistentPtyReadLines
{
    public static PersistentPtyReadLines FromExisting(double value) => From(Require(value));
    public void Deconstruct(out double value) => value = Value;
    public override string ToString() => $"PersistentPtyReadLines {{ Value = {Value} }}";
    private static Vogen.Validation Validate(double value) => double.IsFinite(value) && value == Math.Truncate(value) && value is >= 1 and <= 65535
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Persistent PTY read lines must be an integer from 1 to 65535.");
    internal static double Require(double value)
    {
        MessageContract.Integer(value, 1);
        if (value > 65_535) throw new JsonException("Persistent PTY read lines cannot exceed 65535.");
        return value;
    }
}

public sealed record TerminalScreen(string Text, double Cols, double Rows, TerminalCursor Cursor)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
    [JsonPropertyName("cols"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Cols { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Cols, 1);
    [JsonPropertyName("rows"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Rows { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Rows, 1);
    [JsonPropertyName("cursor"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalCursor>))]
    public TerminalCursor Cursor { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cursor);
}

public sealed record PersistentPtyReadResult(
    PtyId PtyId, string Title, string Cwd,
    [property: JsonPropertyName("foregroundProcess"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ForegroundProcess,
    TerminalScreen Screen
)
{
    [JsonPropertyName("ptyID"), JsonRequired]
    public PtyId PtyId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(PtyId, PtyId.IsInitialized());
    [JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Title { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Title);
    [JsonPropertyName("cwd"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Cwd { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cwd);
    [JsonPropertyName("screen"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TerminalScreen>))]
    public TerminalScreen Screen { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Screen);
}
