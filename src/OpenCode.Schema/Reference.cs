namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReferenceLocalSource), "local")]
[JsonDerivedType(typeof(ReferenceGitSource), "git")]
public abstract record ReferenceSource;

public sealed record ReferenceLocalSource(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("hidden")] bool? Hidden = null
) : ReferenceSource;

public sealed record ReferenceGitSource(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("hidden")] bool? Hidden = null
) : ReferenceSource;

/// <summary>
/// 1:1 port of Reference.Info from packages/schema/src/reference.ts
/// </summary>
public sealed record ReferenceInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source")] ReferenceSource Source,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("hidden")] bool? Hidden = null
);
