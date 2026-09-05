namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record CredentialSwitchedEventData(
    [property: JsonPropertyName("integrationID"), JsonRequired] IntegrationId IntegrationId,
    [property: JsonPropertyName("credentialID"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CredentialId? CredentialId
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        PromptValidation.Required(IntegrationId.Value);
        if (CredentialId is { } id) PromptValidation.Required(id.Value);
    }
}

public sealed record WorktreeUpdatedEventData([property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId)
    : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => PromptValidation.Required(ProjectId.Value);
    void IJsonOnDeserialized.OnDeserialized() => PromptValidation.Required(ProjectId.Value);
}

public sealed record WorktreeResolvedEventData(
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    string Directory,
    [property: JsonPropertyName("previous"), JsonRequired] ProjectId Previous,
    [property: JsonPropertyName("adopted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProjectIdListJsonConverter))] IReadOnlyList<ProjectId>? Adopted = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Directory { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Directory);
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        PromptValidation.Required(ProjectId.Value);
        PromptValidation.Required(Previous.Value);
        if (Adopted is not null)
            foreach (var id in Adopted) PromptValidation.Required(id.Value);
    }
}

public sealed record InstallationVersionEventData(string Version)
{
    [JsonPropertyName("version"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Version { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Version);
}

public sealed record VcsBranchUpdatedEventData(
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Branch = null
);
