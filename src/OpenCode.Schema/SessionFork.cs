namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ForkBoundaryBefore), "before")]
[JsonDerivedType(typeof(ForkBoundaryThrough), "through")]
public abstract record ForkBoundary;

public sealed record ForkBoundaryBefore(
    [property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId
) : ForkBoundary;

public sealed record ForkBoundaryThrough(
    [property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId
) : ForkBoundary;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ForkRequestBoundaryBefore), "before")]
[JsonDerivedType(typeof(ForkRequestBoundaryThrough), "through")]
public abstract record ForkRequestBoundary;

public sealed record ForkRequestBoundaryBefore(
    [property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId
) : ForkRequestBoundary;

public sealed record ForkRequestBoundaryThrough : ForkRequestBoundary;
