namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectionCredentialInfo), "credential")]
[JsonDerivedType(typeof(ConnectionEnvInfo), "env")]
public abstract record ConnectionInfo;

public sealed record ConnectionCredentialInfo(
    CredentialId Id, string Label
) : ConnectionInfo
{
    [JsonPropertyName("id"), JsonRequired]
    public CredentialId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Label { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Label);
}

public sealed record ConnectionEnvInfo(string Name) : ConnectionInfo
{
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
}
