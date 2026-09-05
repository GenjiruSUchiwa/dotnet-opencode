namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ConfigFormatterEntry
{
    [JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Disabled { get; init; }
    [JsonPropertyName("command"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Command { get; init; }
    [JsonPropertyName("environment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] public IReadOnlyDictionary<string, string>? Environment { get; init; }
    [JsonPropertyName("extensions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Extensions { get; init; }
}

[JsonConverter(typeof(ConfigFormatterJsonConverter))]
public abstract record ConfigFormatterInfo
{
    private protected ConfigFormatterInfo() { }
    public sealed record Toggle(bool Value) : ConfigFormatterInfo;
    public sealed record Entries(IReadOnlyDictionary<string, ConfigFormatterEntry> Values) : ConfigFormatterInfo
    {
        public IReadOnlyDictionary<string, ConfigFormatterEntry> Values { get; init => field = ConfigJson.Record(value); } = ConfigJson.Record(Values);
    }
}

[JsonConverter(typeof(ConfigLspEntryJsonConverter))]
public abstract record ConfigLspEntry;
public sealed record ConfigLspDisabled : ConfigLspEntry;
public sealed record ConfigLspServer(IReadOnlyList<string> Command) : ConfigLspEntry
{
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Command { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Command));
    [JsonPropertyName("extensions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Extensions { get; init; }
    [JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Disabled { get; init; }
    [JsonPropertyName("env"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] public IReadOnlyDictionary<string, string>? Env { get; init; }
    [JsonPropertyName("initialization"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] public IReadOnlyDictionary<string, JsonElement>? Initialization { get; init; }
}

[JsonConverter(typeof(ConfigLspJsonConverter))]
public abstract record ConfigLspInfo
{
    private protected ConfigLspInfo() { }
    public sealed record Toggle(bool Value) : ConfigLspInfo;
    public sealed record Entries(IReadOnlyDictionary<string, ConfigLspEntry> Values) : ConfigLspInfo
    {
        public IReadOnlyDictionary<string, ConfigLspEntry> Values { get; init => field = ConfigJson.Record(value); } = ConfigJson.Record(Values);
    }
}

[JsonConverter(typeof(ConfigPluginJsonConverter))]
public abstract record ConfigPlugin
{
    private protected ConfigPlugin() { }
    public sealed record Shorthand(string Value) : ConfigPlugin
    {
        public string Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    }
}

public sealed record ConfigPluginEntry(string Package) : ConfigPlugin
{
    [JsonPropertyName("package"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Package { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Package);
    [JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] public IReadOnlyDictionary<string, JsonElement>? Options { get; init; }
}

[JsonConverter(typeof(ConfigReferenceJsonConverter))]
public abstract record ConfigReferenceEntry
{
    private protected ConfigReferenceEntry() { }
    public sealed record Shorthand(string Value) : ConfigReferenceEntry
    {
        public string Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    }
}

public sealed record ConfigGitReference(string Repository) : ConfigReferenceEntry
{
    [JsonPropertyName("repository"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Repository { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Repository);
    [JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Branch { get; init; }
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Description { get; init; }
    [JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Hidden { get; init; }
}

public sealed record ConfigLocalReference(string Path) : ConfigReferenceEntry
{
    [JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Path { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Path);
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Description { get; init; }
    [JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Hidden { get; init; }
}

[JsonConverter(typeof(ConfigWebSearchJsonConverter))]
public abstract record ConfigWebSearchSelection
{
    private protected ConfigWebSearchSelection() { }
    public sealed record Disabled : ConfigWebSearchSelection;
}

public sealed record ConfigWebSearchInfo(string Provider) : ConfigWebSearchSelection
{
    [JsonPropertyName("provider"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Provider { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Provider);
}

[JsonConverter(typeof(ConfigWarmingJsonConverter))]
public abstract record ConfigWarming
{
    private protected ConfigWarming() { }
    public sealed record Toggle(bool Value) : ConfigWarming;
}

public sealed record ConfigWarmingInfo : ConfigWarming
{
    [JsonPropertyName("prompt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Prompt { get; init; }
    [JsonPropertyName("interval"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigDuration? Interval { get; init; }
    [JsonPropertyName("duration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigDuration? Duration { get; init; }
}
