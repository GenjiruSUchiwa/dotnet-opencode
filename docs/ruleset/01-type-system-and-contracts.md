# Porting Ruleset 01: Type System and Contracts

This rulebook defines how to translate contracts from `@opencode-ai/schema` into idiomatic C# (.NET 10).

---

## 1. Branded Scalar IDs

In TypeScript, IDs are branded strings with runtime prefix validation and ascending ID generation.

### TypeScript Source (`packages/schema/src/session-id.ts`)
```ts
export const ID = Schema.String.check(Schema.isStartsWith("ses_")).pipe(
  Schema.brand("Session.ID"),
  statics((schema) => ({
    create: () => schema.make("ses_" + ascending()),
  })),
)
export type ID = typeof ID.Type
```

### C# Translation
Represent all domain IDs as `readonly record struct` wrapping a `string`. This gives zero-allocation value semantics, strong type safety (preventing accidental parameter swaps), and standard equality:

```csharp
namespace OpenCode.Schema;

[JsonConverter(typeof(SessionIdJsonConverter))]
public readonly record struct SessionId : IEquatable<SessionId>, IComparable<SessionId>
{
    public const string Prefix = "ses_";
    public string Value { get; }

    public SessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"SessionId must start with '{Prefix}', got '{value}'", nameof(value));
        }
        Value = value;
    }

    public static SessionId Create() => new($"{Prefix}{AscendingId.Generate()}");
    public static SessionId FromExisting(string value) => new(value);

    public int CompareTo(SessionId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;

    public static implicit operator string(SessionId id) => id.Value;
    public static explicit operator SessionId(string value) => new(value);
}
```

### JSON Serialization for IDs
Use a dedicated converter or source-generated converter so IDs serialize as raw JSON strings:

```csharp
public sealed class SessionIdJsonConverter : JsonConverter<SessionId>
{
    public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
```

---

## 2. Records and Data Contracts

In TypeScript, records use `Schema.Struct`.

### TypeScript Source (`packages/schema/src/session.ts`)
```ts
export const Info = Schema.Struct({
  id: ID,
  parentID: ID.pipe(optional),
  projectID: Project.ID,
  cost: Money.USD,
  tokens: TokenUsage.Info,
  outcome: Schema.Literals(["succeeded", "failed", "interrupted"]).pipe(optional),
  time: Schema.Struct({
    created: DateTimeUtcFromMillis,
    updated: DateTimeUtcFromMillis,
    idle: DateTimeUtcFromMillis.pipe(optional),
  }),
  title: Schema.String.pipe(optional),
}).annotate({ identifier: "Session.Info" })
```

### C# Translation
Use `sealed record` with primary constructors, init-only properties, and nullable reference types:

```csharp
namespace OpenCode.Schema;

public sealed record SessionInfo(
    SessionId Id,
    ProjectId ProjectId,
    Money Cost,
    TokenUsageInfo Tokens,
    SessionTime Time,
    SessionId? ParentId = null,
    SessionOutcome? Outcome = null,
    string? Title = null,
    LocationRef? Location = null,
    string? Subpath = null,
    SessionMetadata? Metadata = null,
    SessionRevert? Revert = null
);

public sealed record SessionTime(
    DateTimeOffset Created,
    DateTimeOffset Updated,
    DateTimeOffset? Idle = null,
    DateTimeOffset? Viewed = null,
    DateTimeOffset? Archived = null
);
```

### Closed String Literals -> Enums
Convert closed literals to C# enums with `[JsonStringEnumMemberConverter]`:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<SessionOutcome>))]
public enum SessionOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("interrupted")]
    Interrupted
}
```

---

## 3. Discriminated Unions (Polymorphism)

TypeScript models messages and events as discriminated unions with a `type` tag property.

### TypeScript Source (`packages/schema/src/session-message.ts`)
```ts
export const AgentSelected = Schema.Struct({
  ...Base,
  type: Schema.tag("agent-switched"),
  agent: Agent.ID,
  previous: Agent.ID.pipe(optional),
})

export const ModelSelected = Schema.Struct({
  ...Base,
  type: Schema.tag("model-switched"),
  model: Model.Ref,
  previous: Model.Ref.pipe(optional),
})

export type Info = typeof AgentSelected.Type | typeof ModelSelected.Type | ...
```

### C# Translation
Use an `abstract record` root annotated with `[JsonPolymorphic]` and `[JsonDerivedType]`:

```csharp
namespace OpenCode.Schema;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentSelectedMessage), "agent-switched")]
[JsonDerivedType(typeof(ModelSelectedMessage), "model-switched")]
[JsonDerivedType(typeof(UserPromptMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolCallMessage), "tool-call")]
[JsonDerivedType(typeof(ToolResultMessage), "tool-result")]
public abstract record SessionMessage
{
    public required MessageId Id { get; init; }
    public required MessageTime Time { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record AgentSelectedMessage : SessionMessage
{
    public required AgentId Agent { get; init; }
    public AgentId? Previous { get; init; }
}

public sealed record ModelSelectedMessage : SessionMessage
{
    public required ModelRef Model { get; init; }
    public ModelRef? Previous { get; init; }
}

public sealed record UserPromptMessage : SessionMessage
{
    public required IReadOnlyList<ContentPart> Content { get; init; }
}
```

### Pattern Matching / Switch Expressions in C#
Process messages cleanly without manual casts:

```csharp
public static string FormatMessageSummary(SessionMessage message) => message switch
{
    AgentSelectedMessage m => $"Switched agent to {m.Agent}",
    ModelSelectedMessage m => $"Switched model to {m.Model.Id}",
    UserPromptMessage m => $"User: {m.Content.Count} parts",
    AssistantMessage m => $"Assistant response",
    ToolCallMessage m => $"Executing tool {m.ToolName}",
    ToolResultMessage m => $"Tool output for call {m.CallId}",
    _ => throw new UnreachableException($"Unknown message type {message.GetType().Name}")
};
```

---

## 4. System.Text.Json Source Generation (Native AOT)

To guarantee high performance and Native AOT compatibility, define a `JsonSerializerContext`:

```csharp
namespace OpenCode.Schema;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(AgentSelectedMessage))]
[JsonSerializable(typeof(ModelSelectedMessage))]
[JsonSerializable(typeof(UserPromptMessage))]
[JsonSerializable(typeof(OpenCodeEvent))]
public partial class OpenCodeJsonContext : JsonSerializerContext
{
}
```
