namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record ShellInfoEventData(ShellInfo Info)
{
    [JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ShellInfo>))]
    public ShellInfo Info { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Info);
}

public sealed record ShellExitedEventData(ShellId Id, ShellStatus Status, double? Exit = null)
{
    [JsonPropertyName("id"), JsonRequired]
    public ShellId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("status"), JsonRequired]
    public ShellStatus Status { get; init => field = ShellStatusJsonConverter.Validate(value); } = ShellStatusJsonConverter.Validate(Status);
    [JsonPropertyName("exit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFiniteNumberJsonConverter))]
    public double? Exit { get; init => field = value is null ? null : FiniteNumberJsonConverter.Validate(value.Value); } = Exit is null ? null : FiniteNumberJsonConverter.Validate(Exit.Value);
}

public sealed record ShellDeletedEventData(ShellId Id)
{
    [JsonPropertyName("id"), JsonRequired]
    public ShellId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
}

public sealed record PtyInfoEventData(PtyInfo Info)
{
    [JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<PtyInfo>))]
    public PtyInfo Info { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Info);
}

public sealed record PtyExitedEventData(PtyId Id, double ExitCode)
{
    [JsonPropertyName("id"), JsonRequired]
    public PtyId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("exitCode"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double ExitCode { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(ExitCode, 0);
}

public sealed record PtyDeletedEventData(PtyId Id)
{
    [JsonPropertyName("id"), JsonRequired]
    public PtyId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
}

public sealed record PersistentPtyAddedEventData(SessionId SessionId, PersistentPtyInfo Terminal)
{
    [JsonPropertyName("sessionID"), JsonRequired]
    public SessionId SessionId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(SessionId, SessionId.IsInitialized());
    [JsonPropertyName("terminal"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<PersistentPtyInfo>))]
    public PersistentPtyInfo Terminal { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Terminal);
}

public sealed record PersistentPtyRemovedEventData(SessionId SessionId, PtyId PtyId)
{
    [JsonPropertyName("sessionID"), JsonRequired]
    public SessionId SessionId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(SessionId, SessionId.IsInitialized());
    [JsonPropertyName("ptyID"), JsonRequired]
    public PtyId PtyId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(PtyId, PtyId.IsInitialized());
}

public static class ShellEventDefinitions
{
    public static readonly EphemeralEventDefinition<ShellInfoEventData> Created = new("shell.created", OpenCodeJsonContext.Default.ShellInfoEventData);
    public static readonly EphemeralEventDefinition<ShellExitedEventData> Exited = new("shell.exited", OpenCodeJsonContext.Default.ShellExitedEventData);
    public static readonly EphemeralEventDefinition<ShellDeletedEventData> Deleted = new("shell.deleted", OpenCodeJsonContext.Default.ShellDeletedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Created, Exited, Deleted);
}

public static class PtyEventDefinitions
{
    public static readonly EphemeralEventDefinition<PtyInfoEventData> Created = new("pty.created", OpenCodeJsonContext.Default.PtyInfoEventData);
    public static readonly EphemeralEventDefinition<PtyInfoEventData> Updated = new("pty.updated", OpenCodeJsonContext.Default.PtyInfoEventData);
    public static readonly EphemeralEventDefinition<PtyExitedEventData> Exited = new("pty.exited", OpenCodeJsonContext.Default.PtyExitedEventData);
    public static readonly EphemeralEventDefinition<PtyDeletedEventData> Deleted = new("pty.deleted", OpenCodeJsonContext.Default.PtyDeletedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Created, Updated, Exited, Deleted);
}

public static class PersistentPtyEventDefinitions
{
    public static readonly EphemeralEventDefinition<PersistentPtyAddedEventData> Added = new("persistent-pty.added", OpenCodeJsonContext.Default.PersistentPtyAddedEventData);
    public static readonly EphemeralEventDefinition<PersistentPtyRemovedEventData> Removed = new("persistent-pty.removed", OpenCodeJsonContext.Default.PersistentPtyRemovedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Added, Removed);
}
