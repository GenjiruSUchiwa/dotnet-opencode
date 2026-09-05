namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ProjectTime(
    double Created,
    double Updated,
    double? Initialized = null
)
{
    [JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Created { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Created, 0);
    [JsonPropertyName("updated"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Updated { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Updated, 0);
    [JsonPropertyName("initialized"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Initialized { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); } = Initialized is null ? null : MessageContract.Integer(Initialized.Value, 0);
}

public sealed record ProjectIcon(
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Url = null,
    [property: JsonPropertyName("override"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Override = null,
    [property: JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Color = null
);

public sealed record ProjectCommands(
    [property: JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Start = null
);

/// <summary>Canonical Project.Info; project.updated uses these fields directly as its data.</summary>
public sealed record ProjectInfo(
    [property: JsonPropertyName("id"), JsonRequired] ProjectId Id,
    string Canonical,
    ProjectTime Time,
    IReadOnlyList<string> Sandboxes,
    string? Vcs = null,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null,
    [property: JsonPropertyName("icon"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ProjectIcon>))] ProjectIcon? Icon = null,
    [property: JsonPropertyName("commands"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ProjectCommands>))] ProjectCommands? Commands = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("canonical"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Canonical { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Canonical);
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ProjectTime>))]
    public ProjectTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
    [JsonPropertyName("sandboxes"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Sandboxes { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Sandboxes));
    [JsonPropertyName("vcs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProjectVcsJsonConverter))]
    public string? Vcs { get; init => field = value is null ? null : ProjectVcsJsonConverter.Validate(value); } = Vcs is null ? null : ProjectVcsJsonConverter.Validate(Vcs);

    void IJsonOnSerializing.OnSerializing() => PromptValidation.Required(Id.Value);
    void IJsonOnDeserialized.OnDeserialized() => PromptValidation.Required(Id.Value);
}
