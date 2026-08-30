namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ForkBoundaryBefore), "before")]
[JsonDerivedType(typeof(ForkBoundaryThrough), "through")]
public abstract record ForkBoundary;

public sealed record ForkBoundaryBefore(
    [property: JsonPropertyName("messageID")] MessageId MessageId
) : ForkBoundary;

public sealed record ForkBoundaryThrough(
    [property: JsonPropertyName("messageID")] MessageId MessageId
) : ForkBoundary;
