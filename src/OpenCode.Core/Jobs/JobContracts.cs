namespace OpenCode.Core.Jobs;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

[JsonConverter(typeof(JobStatusJsonConverter))]
public enum JobStatus { Running, Completed, Error, Cancelled }

public sealed class JobStatusJsonConverter : JsonConverter<JobStatus>
{
    public override JobStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType != JsonTokenType.String ? throw new JsonException("Job status must be a string.") : reader.GetString() switch
        {
            "running" => JobStatus.Running, "completed" => JobStatus.Completed, "error" => JobStatus.Error, "cancelled" => JobStatus.Cancelled,
            _ => throw new JsonException("Unknown job status.")
        };
    public override void Write(Utf8JsonWriter writer, JobStatus value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        JobStatus.Running => "running", JobStatus.Completed => "completed", JobStatus.Error => "error", JobStatus.Cancelled => "cancelled",
        _ => throw new JsonException("Unknown job status.")
    });
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JobShellRecovery), "shell")]
[JsonDerivedType(typeof(JobSubagentRecovery), "subagent")]
public abstract record JobRecovery
{
    // This is mutation coordination metadata for the store, not another persisted field.
    [JsonIgnore] public abstract SessionId OwnerSessionId { get; }
    public abstract void Validate();
}

public sealed record JobShellRecovery(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("shellID"), JsonRequired] ShellId ShellId,
    [property: JsonPropertyName("command"), JsonRequired] string Command) : JobRecovery
{
    [JsonIgnore] public override SessionId OwnerSessionId => SessionId;
    public override void Validate()
    {
        _ = OpenCode.Schema.SessionId.FromExisting(SessionId.Value);
        _ = OpenCode.Schema.ShellId.FromExisting(ShellId.Value);
        if (Command is null) throw new JsonException("A shell recovery command is required.");
    }
}

public sealed record JobSubagentRecovery(
    [property: JsonPropertyName("parentSessionID"), JsonRequired] SessionId ParentSessionId,
    [property: JsonPropertyName("childSessionID"), JsonRequired] SessionId ChildSessionId,
    [property: JsonPropertyName("agent"), JsonRequired] string Agent,
    [property: JsonPropertyName("description"), JsonRequired] string Description) : JobRecovery
{
    [JsonIgnore] public override SessionId OwnerSessionId => ChildSessionId;
    public override void Validate()
    {
        _ = SessionId.FromExisting(ParentSessionId.Value);
        _ = SessionId.FromExisting(ChildSessionId.Value);
        if (Agent is null || Description is null) throw new JsonException("Subagent recovery requires agent and description.");
    }
}

/// <summary>Source KV job.background marker. It is not an execution claim or a durable event.</summary>
public sealed record JobBackground(
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("notificationID"), JsonRequired] MessageId NotificationId,
    [property: JsonPropertyName("recovery"), JsonRequired] JobRecovery Recovery,
    [property: JsonPropertyName("status"), JsonRequired] JobStatus Status,
    [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Output = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Error = null)
    : IJsonOnDeserialized, IJsonOnSerializing
{
    public const string Prefix = "job.background/";
    [JsonIgnore] public string Key => Prefix + NotificationId.Value;
    public void Validate()
    {
        if (Id is null || Recovery is not (JobShellRecovery or JobSubagentRecovery) || !Enum.IsDefined(Status)) throw new JsonException("Invalid background job marker.");
        _ = MessageId.FromExisting(NotificationId.Value);
        Recovery.Validate();
    }
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    void IJsonOnSerializing.OnSerializing() => Validate();
}

/// <summary>
/// Implement over the existing channel KV/event-store coordination. List validates each descriptor
/// and key/notification identity, skipping malformed/unknown rows without deleting them. Save and
/// Remove complete their committed transaction before reporting success; there is no memory fallback.
/// </summary>
public interface IJobBackgroundStore
{
    Task<IReadOnlyList<JobBackground>> ListAsync(CancellationToken ct);
    Task SaveAsync(JobBackground value, CancellationToken ct);
    Task RemoveAsync(MessageId notificationId, CancellationToken ct);
}

public sealed record JobInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] JobStatus Status,
    [property: JsonPropertyName("started_at")] double StartedAt,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyName("completed_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CompletedAt = null,
    [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Output = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("notificationID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MessageId? NotificationId = null);

public sealed record JobStartInput(string Type, Func<CancellationToken, Task<string>> Run,
    string? Id = null, string? Title = null, IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    JobRecovery? Recovery = null, MessageId? NotificationId = null);
public sealed record JobWaitResult(JobInfo? Info, bool TimedOut);
public sealed record JobBlockResult(JobInfo Info, bool Backgrounded);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(JobBackground))]
[JsonSerializable(typeof(JobRecovery))]
[JsonSerializable(typeof(JobInfo))]
public partial class JobJsonContext : JsonSerializerContext;
