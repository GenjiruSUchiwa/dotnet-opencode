namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReferenceLocalSource), "local")]
[JsonDerivedType(typeof(ReferenceGitSource), "git")]
public abstract record ReferenceSource;

public sealed record ReferenceLocalSource(
    [property: JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Path,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Hidden = null
) : ReferenceSource, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Path);
}

public sealed record ReferenceGitSource(
    [property: JsonPropertyName("repository"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Repository,
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Branch = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Hidden = null
) : ReferenceSource, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Repository);
}

/// <summary>
/// 1:1 port of Reference.Info from packages/schema/src/reference.ts
/// </summary>
public sealed record ReferenceInfo(
    [property: JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Name,
    [property: JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Path,
    [property: JsonPropertyName("source"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ReferenceSource>))] ReferenceSource Source,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Hidden = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Name, Path, Source);
}
