namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Errors;
using OpenCode.Schema;

public sealed record ApiResult<T>([property: JsonPropertyName("data"), JsonRequired] T Data) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Data is null) throw new JsonException("Response requires non-null data.");
    }
}

public sealed record ApiCursor(
    [property: JsonPropertyName("previous"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Previous = null,
    [property: JsonPropertyName("next"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Next = null);

public sealed record ApiPage<T>(
    [property: JsonPropertyName("data"), JsonRequired] IReadOnlyList<T> Data,
    [property: JsonPropertyName("cursor"), JsonRequired] ApiCursor Cursor) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Data is null || Cursor is null) throw new JsonException("Page requires non-null data and cursor.");
    }
}

public sealed record SessionCreateInput(
    [property: JsonPropertyName("id")] SessionId? Id = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null,
    [property: JsonPropertyName("location")] LocationRef? Location = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record SessionPromptInput(
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("id")] MessageId? Id = null,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptInputFileAttachment>? Files = null,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptInputSkillAttachment>? Skills = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode? Delivery = null,
    [property: JsonPropertyName("resume")] bool? Resume = null);

public sealed record InterruptSessionResponse([property: JsonPropertyName("interrupted"), JsonRequired] bool Interrupted);

public sealed record SessionViewInput(
    [property: JsonPropertyName("idle"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))] double Idle);

public sealed record SessionEnvironmentInput(
    [property: JsonPropertyName("variables"), JsonRequired] IReadOnlyDictionary<string, string> Variables)
    : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        if (Variables is null || Variables.Any(pair => pair.Key is null || pair.Value is null))
            throw new JsonException("Environment variables must be a non-null record of string values.");
    }
}

public sealed record SessionRenameInput(
    [property: JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Title);

public sealed record SessionSwitchAgentInput([property: JsonPropertyName("agent"), JsonRequired] AgentId Agent)
    : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        if (!Agent.IsInitialized()) throw new JsonException("Agent must be an initialized string identifier.");
    }
}

public sealed record SessionSwitchModelInput(
    [property: JsonPropertyName("model"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))] ModelRef Model);

public sealed record SessionActive([property: JsonPropertyName("type"), JsonRequired] string Type)
    : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        if (Type != "running") throw new JsonException("Active session state must be running.");
    }
}

public enum SessionOrder { Ascending, Descending }

/// <summary>Flat HTTP query fields from protocol/groups/session.ts. Cursor content belongs to the server.</summary>
public sealed record SessionListQuery
{
    public WorkspaceId? Workspace { get; init; }
    public double? Limit { get; init; }
    public SessionOrder? Order { get; init; }
    public string? Search { get; init; }
    public SessionId? ParentId { get; init; }
    /// <summary>Emits parentID=null, distinct from omitting the parent filter.</summary>
    public bool RootOnly { get; init; }
    public string? Directory { get; init; }
    public ProjectId? Project { get; init; }
    public string? Subpath { get; init; }
    /// <summary>Opaque server cursor. When supplied, upstream replaces filters/order with its stored query; limit remains independent.</summary>
    public string? Cursor { get; init; }

    public static SessionListQuery All() => new();
    public static SessionListQuery ForDirectory(string directory) => new() { Directory = directory ?? throw new ArgumentNullException(nameof(directory)) };
    public static SessionListQuery ForProject(ProjectId project, string? subpath = null) => new() { Project = project, Subpath = subpath };
    public static SessionListQuery FromCursor(string cursor, double? limit = null) => new()
    {
        Cursor = cursor ?? throw new ArgumentNullException(nameof(cursor)), Limit = limit
    };

    public void Validate()
    {
        if (Limit is double limit && (!double.IsFinite(limit) || limit <= 0 || Math.Truncate(limit) != limit))
            throw SessionQueryValidationException.InvalidRequest("Limit must be a positive finite integer.", "limit");
        if (Order is not (null or SessionOrder.Ascending or SessionOrder.Descending))
            throw SessionQueryValidationException.InvalidRequest("Order must be asc or desc.", "order");
        if (RootOnly && ParentId is not null)
            throw SessionQueryValidationException.InvalidRequest("RootOnly and ParentId cannot both select the parentID filter.", "parentID");
        if (ParentId is SessionId parent && !parent.IsInitialized())
            throw SessionQueryValidationException.InvalidRequest("ParentId must not be an uninitialized identifier.", "parentID");
        if (Project is ProjectId project && !project.IsInitialized())
            throw SessionQueryValidationException.InvalidRequest("Project must not be an uninitialized identifier.", "project");
        if (Workspace is WorkspaceId workspace && !workspace.IsInitialized())
            throw SessionQueryValidationException.InvalidRequest("Workspace must not be an uninitialized identifier.", "workspace");
        // The public HTTP schema permits flat combinations. Do not invent path
        // validation, reject combined directory/project fields, or inspect cursors.
    }
}

public sealed record SessionMessagesQuery(int? Limit = null, SessionOrder? Order = null, string? Cursor = null)
{
    public static SessionMessagesQuery FromCursor(string cursor, int? limit = null) =>
        new(limit, Cursor: cursor ?? throw new ArgumentNullException(nameof(cursor)));

    public void Validate()
    {
        if (Limit is < 1 or > 200)
            throw SessionQueryValidationException.InvalidRequest("Message limit must be between 1 and 200.", "limit");
        if (Order is not (null or SessionOrder.Ascending or SessionOrder.Descending))
            throw SessionQueryValidationException.InvalidRequest("Order must be asc or desc.", "order");
        // handlers/message.ts checks cursor truthiness, unlike the session-list handler.
        if (!string.IsNullOrEmpty(Cursor) && Order is not null)
            throw new SessionQueryValidationException(new InvalidCursorError { Message = "Cursor cannot be combined with order" }, "cursor");
    }
}
