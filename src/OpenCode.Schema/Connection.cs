namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectionCredentialInfo), "credential")]
[JsonDerivedType(typeof(ConnectionEnvInfo), "env")]
public abstract record ConnectionInfo;

public sealed record ConnectionCredentialInfo(
    [property: JsonPropertyName("id")] CredentialId Id,
    [property: JsonPropertyName("label")] string Label
) : ConnectionInfo;

public sealed record ConnectionEnvInfo(
    [property: JsonPropertyName("name")] string Name
) : ConnectionInfo;
