namespace OpenCode.Server.Documentation;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenCode.Schema;
using OpenCode.Protocol.Groups;

internal sealed class UnmappedSchemaException(Type type, string detail) : InvalidOperationException($"{type.FullName}: {detail}");

/// <summary>Pure metadata export. Custom codecs are mapped explicitly; an unknown typed codec fails rather than becoming {}.</summary>
internal sealed class NativeSchemaExporter(JsonObject components)
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(), RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true
    };
    private readonly HashSet<Type> _built = [];
    private readonly HashSet<Type> _building = [];
    private readonly Dictionary<string, Type> _names = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Stable = new(StringComparer.Ordinal)
    {
        [nameof(SessionInfo)] = "Session.Info", [nameof(SessionMessage)] = "Session.Message.Info",
        [nameof(UserMessage)] = "Session.Message.User", [nameof(AssistantMessage)] = "Session.Message.Assistant",
        [nameof(SystemMessage)] = "Session.Message.System", [nameof(SyntheticMessage)] = "Session.Message.Synthetic",
        [nameof(ShellMessage)] = "Session.Message.Shell", [nameof(SkillMessage)] = "Session.Message.Skill",
        [nameof(AgentSelectedMessage)] = "Session.Message.AgentSelected", [nameof(ModelSelectedMessage)] = "Session.Message.ModelSelected",
        [nameof(LocationSwitchedMessage)] = "Session.Message.LocationSwitched", [nameof(CompactionMessage)] = "Session.Message.Compaction",
        [nameof(CompactionRunningMessage)] = "Session.Message.Compaction.Running", [nameof(CompactionCompletedMessage)] = "Session.Message.Compaction.Completed",
        [nameof(CompactionFailedMessage)] = "Session.Message.Compaction.Failed", [nameof(SessionInboxItem)] = "Session.Inbox.Info",
        [nameof(UserInboxPayload)] = "Session.Inbox.UserPayload", [nameof(SyntheticInboxPayload)] = "Session.Inbox.SyntheticPayload",
        [nameof(CompactionInboxPayload)] = "Session.Inbox.CompactionPayload", [nameof(MoveInboxPayload)] = "Session.Inbox.MovePayload",
        [nameof(InboxDeliveryMode)] = "Session.Inbox.Delivery", [nameof(SessionRevert)] = "Session.Revert",
        [nameof(ModelInfo)] = "Model.Info", [nameof(ModelRef)] = "Model.Ref", [nameof(ProviderInfo)] = "Provider.Info",
        [nameof(AgentInfo)] = "Agent.Info", [nameof(LocationInfo)] = "Location.InfoEncoded", [nameof(LocationRef)] = "Location.Ref",
        [nameof(ModelCapabilities)] = "Model.Capabilities", [nameof(ModelCompatibility)] = "Model.Compatibility",
        [nameof(ModelCost)] = "Model.Cost", [nameof(ModelVariant)] = "Model.Variant",
        [nameof(AssistantTextContent)] = "Session.Message.Assistant.Text", [nameof(AssistantReasoningContent)] = "Session.Message.Assistant.Reasoning",
        [nameof(AssistantToolContent)] = "Session.Message.Assistant.Tool", [nameof(ToolStateStreaming)] = "Session.Message.ToolState.Streaming",
        [nameof(ToolStateRunning)] = "Session.Message.ToolState.Running", [nameof(ToolStateCompleted)] = "Session.Message.ToolState.Completed",
        [nameof(ToolStateError)] = "Session.Message.ToolState.Error", [nameof(SessionStructuredError)] = "Session.StructuredError",
        [nameof(IntegrationInfo)] = "Integration.Info", [nameof(IntegrationAttempt)] = "Integration.AttemptEncoded",
        [nameof(IntegrationAttemptStatus)] = "Integration.AttemptStatus", [nameof(SessionTransferData)] = "SessionTransfer.Data",
        [nameof(SessionStatsInfo)] = "SessionStats.Info",
        [nameof(LocationProjectInfo)] = "Project.Current", [nameof(ProjectInfo)] = "Project", [nameof(ProjectIcon)] = "Project.Icon",
        [nameof(ProjectCommands)] = "Project.Commands", [nameof(ProjectTime)] = "Project.Time", [nameof(WorktreeInfo)] = "Worktree.Info",
        [nameof(WorktreeDirectory)] = "Worktree.Directory", [nameof(CommandInfo)] = "Command.Info", [nameof(SkillInfo)] = "Skill.Info",
        [nameof(ReferenceInfo)] = "Reference.Info", [nameof(VcsInfo)] = "Vcs.Info", [nameof(VcsBranch)] = "Vcs.Branch",
        [nameof(VcsBase)] = "Vcs.Base", [nameof(VcsDiffMode)] = "Vcs.Mode", [nameof(VcsFileStatus)] = "Vcs.FileStatus",
        [nameof(FileDiffInfo)] = "FileDiff.Info", [nameof(PtyInfo)] = "Pty", [nameof(PtyConnectToken)] = "PtyTicket.ConnectToken",
        [nameof(PersistentPtyInfo)] = "PersistentPty.Info", [nameof(PersistentPtySnapshot)] = "PersistentPty.Snapshot",
        [nameof(PersistentPtyReadResult)] = "PersistentPty.ReadResult", [nameof(PersistentPtyHandoff)] = "PersistentPty.Handoff",
        [nameof(PersistentPtyCreateInput)] = "PersistentPty.CreateInput", [nameof(PersistentPtyUpdateInput)] = "PersistentPty.UpdateInput",
        [nameof(ShellInfo)] = "Shell.Info", [nameof(FormInfo)] = "Form.Info", [nameof(FormAnswer)] = "Form.Answer",
        [nameof(FormValue)] = "Form.Value", [nameof(FormState)] = "Form.State", [nameof(FormField)] = "Form.Field",
        [nameof(FormStringField)] = "Form.StringField", [nameof(FormBooleanField)] = "Form.BooleanField",
        [nameof(FormNumberField)] = "Form.NumberField", [nameof(FormIntegerField)] = "Form.IntegerField",
        [nameof(FormMultiselectField)] = "Form.MultiselectField", [nameof(FormExternalField)] = "Form.ExternalField",
        [nameof(FormOption)] = "Form.Option", [nameof(FormWhen)] = "Form.When", [nameof(FormReply)] = "Form.Reply",
        [nameof(FormCreatePayload)] = "Form.CreatePayload", [nameof(PermissionRequest)] = "Permission.Request",
        [nameof(PermissionSource)] = "Permission.Source", [nameof(PermissionRule)] = "Permission.Rule",
        [nameof(PermissionEffect)] = "Permission.Effect", [nameof(PermissionReply)] = "Permission.Reply",
        [nameof(PermissionSavedInfo)] = "PermissionSaved.Info", [nameof(TokenUsageInfo)] = "TokenUsage.Info",
        [nameof(Money)] = "Money.USD", [nameof(McpServer)] = "Mcp.Server", [nameof(McpResourceCatalog)] = "Mcp.ResourceCatalog",
        [nameof(McpResource)] = "Mcp.Resource", [nameof(McpResourceTemplate)] = "Mcp.ResourceTemplate",
        [nameof(McpLocalConfig)] = "Mcp.LocalConfigEncoded", [nameof(McpRemoteConfig)] = "Mcp.RemoteConfigEncoded",
        [nameof(McpOAuthConfig)] = "Mcp.OAuthConfigEncoded", [nameof(McpConnectedStatus)] = "Mcp.Status.Connected",
        [nameof(McpPendingStatus)] = "Mcp.Status.Pending", [nameof(McpDisabledStatus)] = "Mcp.Status.Disabled",
        [nameof(McpFailedStatus)] = "Mcp.Status.Failed", [nameof(McpNeedsAuthStatus)] = "Mcp.Status.NeedsAuth",
        [nameof(ConfigEntry)] = "Config.Entry", [nameof(ConfigDocument)] = "Config.DocumentEncoded",
        [nameof(ConfigDirectory)] = "Config.DirectoryEncoded", [nameof(ConfigAgentsDirectory)] = "Config.AgentsDirectoryEncoded",
        [nameof(ConfigClaudeDirectory)] = "Config.ClaudeDirectoryEncoded", [nameof(OpenCodeConfiguration)] = "Config.InfoEncoded"
    };

    internal JsonNode Reference(Type type)
    {
        var name = Name(type);
        if (_built.Contains(type) || _building.Contains(type)) return Ref(name);
        if (_names.TryGetValue(name, out var existing) && existing != type) throw new UnmappedSchemaException(type, "schema identifier collision");
        _names[name] = type;
        _building.Add(type);
        try
        {
            var schema = Custom(type, null) ?? _options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = (context, node) => Transform(type, context, node)
            });
            if (schema is JsonObject empty && empty.Count == 0) throw new UnmappedSchemaException(type, "empty schema from a typed contract");
            AddDiscriminator(type, schema);
            RewriteLocalRefs(schema, name);
            components[name] = schema;
            _built.Add(type);
            return Ref(name);
        }
        catch (JsonException error) { throw new UnmappedSchemaException(type, "codec metadata export failed: " + error.Message); }
        finally { _building.Remove(type); }
    }

    private JsonNode Transform(Type root, JsonSchemaExporterContext context, JsonNode node)
    {
        var type = context.TypeInfo.Type;
        var converter = context.PropertyInfo?.CustomConverter;
        var custom = Custom(type, converter);
        if (custom is not null) node = custom;
        else if (context.TypeInfo.Kind == JsonTypeInfoKind.None && !type.IsPrimitive && type != typeof(decimal)
            && type != typeof(string) && type != typeof(DateTimeOffset) && type != typeof(DateTime) && type != typeof(Guid)
            && type != typeof(Uri) && type != typeof(byte[]))
            throw new UnmappedSchemaException(type, $"unmapped converter {converter?.GetType().Name ?? context.TypeInfo.Converter.GetType().Name}");

        if (node is JsonObject record && record["type"]?.ToJsonString() == "\"object\"" && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
            record["additionalProperties"] ??= false;
        if (context.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true)
            .OfType<JsonIgnoreAttribute>().Any(attribute => attribute.Condition == JsonIgnoreCondition.WhenWritingNull) == true)
            node = NonNull(node);
        return node;
    }

    private JsonNode? Custom(Type original, JsonConverter? converter)
    {
        if (Nullable.GetUnderlyingType(original) is { } nullable && converter is null)
            return Any(Reference(nullable), new JsonObject { ["type"] = "null" });
        var type = Nullable.GetUnderlyingType(original) ?? original;
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var argument = type.GetGenericArguments()[0];
            if (definition == typeof(ApiResult<>)) return Object(new JsonObject { ["data"] = Reference(argument) }, ["data"]);
            if (definition == typeof(ApiPage<>)) return Object(new JsonObject { ["data"] = Array(Reference(argument)), ["cursor"] = Reference(typeof(ApiCursor)) }, ["data", "cursor"]);
            if (definition == typeof(LocationResponse<>)) return Object(new JsonObject { ["location"] = Reference(typeof(LocationInfo)),
                ["data"] = argument == typeof(VcsBase) ? Any(Reference(argument), new JsonObject { ["type"] = "null" }) : Reference(argument) }, ["location", "data"]);
        }
        var codec = converter?.GetType() ?? _options.GetTypeInfo(original).Converter.GetType();
        var name = codec.Name.Split('`')[0];
        if (codec.IsGenericType)
        {
            var argument = codec.GetGenericArguments()[0];
            if (name is "NonNullPromptJsonConverter" or "OptionalValueJsonConverter") return Reference(argument);
            if (name == "PromptAttachmentListJsonConverter") return Array(Reference(argument));
            if (name == "ConfigRecordJsonConverter") return Map(Reference(argument));
        }
        if (name is "PositiveIntegerJsonConverter" or "OptionalPositiveIntegerJsonConverter") return Number(true, 1);
        if (name is "NonNegativeIntegerJsonConverter" or "OptionalNonNegativeIntegerJsonConverter") return Number(true, 0);
        if (name is "IntegerNumberJsonConverter" or "OptionalIntegerNumberJsonConverter") return Number(true);
        if (name is "FiniteNumberJsonConverter" or "OptionalFiniteNumberJsonConverter") return Number(false);
        if (name is "SchemaNumberJsonConverter" or "OptionalFormNumberJsonConverter") return Any(Number(false), Strings("NaN", "Infinity", "-Infinity"));
        if (name is "EpochMillisecondsJsonConverter" or "OptionalEpochMillisecondsJsonConverter") return Epoch();
        if (name is "CreatedMessageTimeJsonConverter" or "CompletedMessageTimeJsonConverter")
        {
            var properties = new JsonObject { ["created"] = Epoch(), ["completed"] = Epoch() };
            return Object(properties, name == "CompletedMessageTimeJsonConverter" ? ["created", "completed"] : ["created"]);
        }
        if (name == "OptionalHttpStatusJsonConverter") return Number(true, 100, 599);
        if (name == "McpCallbackPortJsonConverter") return Number(true, 1, 65535);
        if (name == "ProviderHeadersJsonConverter") return Map(String());
        if (name == "ProjectVcsJsonConverter") return new JsonObject { ["type"] = "string", ["pattern"] = "^[a-z][a-z0-9._-]*$" };
        if (name == "ConfigColorJsonConverter") return new JsonObject { ["type"] = "string", ["pattern"] = "^#[a-fA-F0-9]{6}$" };
        if (name is "PtyCheckpointJsonConverter" or "PromptBase64JsonConverter") return new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" };
        if (name == "FormFieldsJsonConverter") return Array(Reference(typeof(FormField)), 1);
        if (name == "NonEmptyToolContentJsonConverter") return Array(Reference(typeof(ToolContent)), 1);
        if (name == "ProjectIdListJsonConverter") return Array(Reference(typeof(ProjectId)));
        if (name == "McpServersJsonConverter") return Map(Reference(typeof(McpServerConfig)));
        if (name == "WorktreeTrimmedStringConverter") return new JsonObject { ["type"] = "string", ["minLength"] = 1, ["x-native-normalization"] = "ECMAScript trim" };
        if (name == "OptionalLlmFinishReasonJsonConverter") return Reference(typeof(LlmFinishReason));
        if (name == "EventLocationJsonConverter") return Any(Reference(typeof(LocationRef)), new JsonObject { ["type"] = "null" });
        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(object)) return JsonValue.Create(true)!;
        if (type == typeof(JsonObject)) return Map(JsonValue.Create(true)!);
        if (type == typeof(SessionId)) return String("^ses");
        if (type == typeof(FormId)) return String("^frm_");
        if (type == typeof(PtyId)) return String("^pty");
        if (type == typeof(ShellId)) return String("^sh_");
        if (type == typeof(EventId)) return String("^evt_");
        if (type == typeof(MessageId) || type == typeof(PermissionId) || type == typeof(WorkspaceId) || type == typeof(PermissionSavedId))
            return new JsonObject { ["type"] = "string", ["minLength"] = 1, ["x-native-non-whitespace"] = true };
        // These brands are Schema.String, not prefix-constrained or nullable scalars.
        // Optional outer fields are handled by their omission/nullable codecs above.
        if (type == typeof(PluginId) || type == typeof(SnapshotId) || type == typeof(SkillId)) return String();
        if (type == typeof(ProjectId) || type == typeof(AgentId) || type == typeof(ProviderId) || type == typeof(ModelId)
            || type == typeof(VariantId) || type == typeof(CredentialId) || type == typeof(IntegrationId) || type == typeof(IntegrationMethodId)
            || type == typeof(IntegrationAttemptId) || type == typeof(ModelFamily)) return String();
        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var value in Enum.GetValues(type)) values.Add(JsonSerializer.SerializeToNode(value, type, _options));
            var schema = new JsonObject { ["enum"] = values };
            if (values.All(value => value is JsonValue item && item.TryGetValue<string>(out _))) schema["type"] = "string";
            return Nullable.GetUnderlyingType(original) is not null && converter is null ? Any(schema, new JsonObject { ["type"] = "null" }) : schema;
        }
        if (type == typeof(Money) || type == typeof(MoneyPerMillionTokens)) return Number(false);
        if (type == typeof(PersistentPtyReadLines)) return Number(true, 1, 65535);
        if (type == typeof(FormValue)) return Any(String(), Number(false), new JsonObject { ["type"] = "boolean" }, Array(String()));
        if (type == typeof(FormConditionValue)) return Any(String(), Number(false), new JsonObject { ["type"] = "boolean" });
        if (type == typeof(FormAnswer)) return Map(Reference(typeof(FormValue)));
        if (type == typeof(TokenCacheUsage)) return Object(new JsonObject { ["read"] = Number(false), ["write"] = Number(false) }, ["read", "write"]);
        if (type == typeof(TokenUsageInfo)) return Object(new JsonObject { ["input"] = Number(false), ["output"] = Number(false),
            ["reasoning"] = Number(false), ["cache"] = Reference(typeof(TokenCacheUsage)) }, ["input", "output", "reasoning", "cache"]);
        if (type == typeof(SessionMessage)) return Union("type", typeof(UserMessage), typeof(SyntheticMessage), typeof(SystemMessage), typeof(SkillMessage),
            typeof(ShellMessage), typeof(AssistantMessage), typeof(AgentSelectedMessage), typeof(ModelSelectedMessage), typeof(LocationSwitchedMessage), typeof(CompactionMessage));
        if (type == typeof(CompactionMessage)) return Union("status", typeof(CompactionRunningMessage), typeof(CompactionCompletedMessage), typeof(CompactionFailedMessage));
        if (type == typeof(SessionInboxItem) || type == typeof(InboxItem)) return Inbox(type == typeof(SessionInboxItem));
        if (type == typeof(OpenCodeEvent)) return Object(new JsonObject
        {
            ["id"] = Reference(typeof(EventId)), ["type"] = String(), ["created"] = Number(false),
            ["data"] = Map(JsonValue.Create(true)!), ["location"] = Reference(typeof(LocationRef)),
            ["metadata"] = Map(JsonValue.Create(true)!), ["durable"] = Reference(typeof(DurableEnvelope))
        }, ["id", "type", "created", "data"]);
        if (type == typeof(McpOAuthSetting)) return Any(new JsonObject { ["const"] = false }, Reference(typeof(McpOAuthConfig)));
        if (type == typeof(McpServerConfig)) return Union("type", typeof(McpLocalConfig), typeof(McpRemoteConfig));
        if (type == typeof(McpConfiguration)) return RecordMetadata(type);
        if (type == typeof(McpRemoteConfig)) return Object(new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = "remote" }, ["url"] = String(), ["headers"] = Map(String()),
            ["disabled"] = new JsonObject { ["type"] = "boolean" }, ["codemode"] = new JsonObject { ["type"] = "boolean" },
            ["timeout"] = Reference(typeof(McpTimeoutConfig)), ["oauth"] = Reference(typeof(McpOAuthSetting))
        }, ["type", "url"]);
        if (type == typeof(ConfigDuration)) return new JsonObject { ["type"] = "string", ["description"] = "Encoded duration (millis/nanos) or signed Infinity." };
        if (type == typeof(ConfigAutoUpdate)) return Any(new JsonObject { ["type"] = "boolean" }, Strings("notify"));
        if (type == typeof(ConfigEntry)) return Union("type", typeof(ConfigDocument), typeof(ConfigDirectory), typeof(ConfigAgentsDirectory), typeof(ConfigClaudeDirectory));
        if (type == typeof(ConfigDocument)) return Object(new JsonObject { ["type"] = new JsonObject { ["const"] = "document" },
            ["info"] = Reference(typeof(OpenCodeConfiguration)), ["path"] = String() }, ["type", "info"]);
        if (type == typeof(OpenCodeConfiguration)) return RecordMetadata(type);
        if (type == typeof(ConfigModelSelection)) return Object(new JsonObject { ["providerID"] = String(), ["model"] = String(), ["variant"] = String() }, ["providerID", "model"]);
        if (type == typeof(ConfigFormatterInfo)) return Any(new JsonObject { ["type"] = "boolean" }, Map(Reference(typeof(ConfigFormatterEntry))));
        if (type == typeof(ConfigLspInfo)) return Any(new JsonObject { ["type"] = "boolean" }, Map(Reference(typeof(ConfigLspEntry))));
        if (type == typeof(ConfigLspEntry)) return Any(Object(new JsonObject { ["disabled"] = new JsonObject { ["const"] = true } }, ["disabled"]), Reference(typeof(ConfigLspServer)));
        if (type == typeof(ConfigPlugin)) return Any(String(), Reference(typeof(ConfigPluginEntry)));
        if (type == typeof(ConfigReferenceEntry)) return Any(String(), Reference(typeof(ConfigGitReference)), Reference(typeof(ConfigLocalReference)));
        if (type == typeof(ConfigModelCosts)) return Any(Reference(typeof(ConfigModelCost)), Array(Reference(typeof(ConfigModelCost))));
        if (type == typeof(ConfigWebSearchSelection)) return Any(new JsonObject { ["const"] = false }, Reference(typeof(ConfigWebSearchInfo)));
        if (type == typeof(ConfigWarming)) return Any(new JsonObject { ["type"] = "boolean" }, Reference(typeof(ConfigWarmingInfo)));
        if (type.GetCustomAttribute<JsonPolymorphicAttribute>() is { } polymorphic)
            return Union(polymorphic.TypeDiscriminatorPropertyName ?? "$type", type.GetCustomAttributes<JsonDerivedTypeAttribute>().Select(derived => derived.DerivedType).ToArray());
        if ((converter ?? _options.GetTypeInfo(original).Converter) is IScalarJsonConverter scalar)
            throw new UnmappedSchemaException(type, $"unmapped scalar codec with underlying {scalar.ScalarType.FullName}");
        return null;
    }

    private JsonNode RecordMetadata(Type type)
    {
        // The framework exporter tries to serialize optional constructor defaults.
        // Config's omit-only custom codecs correctly reject null defaults. Export
        // those properties from the SAME JsonTypeInfo instead of relaxing codecs
        // or calling their Write methods with an invalid value.
        var properties = new JsonObject();
        var required = new List<string>();
        foreach (var property in _options.GetTypeInfo(type).Properties)
        {
            if (property.Get is null) continue;
            if (property.IsExtensionData) throw new UnmappedSchemaException(type, "extension-data metadata needs an explicit codec contract");
            var schema = Custom(property.PropertyType, property.CustomConverter) ?? Reference(property.PropertyType);
            if (property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).OfType<JsonIgnoreAttribute>()
                .Any(attribute => attribute.Condition == JsonIgnoreCondition.WhenWritingNull) == true) schema = NonNull(schema);
            properties[property.Name] = schema;
            if (property.IsRequired || property.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false }) required.Add(property.Name);
        }
        return Object(properties, required);
    }

    private JsonNode Inbox(bool enqueued)
    {
        var variants = new JsonArray();
        foreach (var (name, payload) in new[] { ("user", typeof(UserInboxPayload)), ("synthetic", typeof(SyntheticInboxPayload)),
            ("compaction", typeof(CompactionInboxPayload)), ("move", typeof(MoveInboxPayload)) })
        {
            var properties = new JsonObject { ["type"] = new JsonObject { ["const"] = name }, ["delivery"] = Reference(typeof(InboxDeliveryMode)), ["payload"] = Reference(payload) };
            var required = new List<string> { "type", "delivery", "payload" };
            if (enqueued)
            {
                properties["id"] = Reference(typeof(MessageId)); properties["sessionID"] = Reference(typeof(SessionId)); properties["timeCreated"] = Epoch();
                required.AddRange(["id", "sessionID", "timeCreated"]);
            }
            variants.Add(Object(properties, required));
        }
        return new JsonObject { ["oneOf"] = variants, ["discriminator"] = new JsonObject { ["propertyName"] = "type" } };
    }

    private JsonNode Union(string discriminator, params Type[] variants) => new JsonObject
    {
        ["oneOf"] = new JsonArray(variants.Select(Reference).ToArray()), ["discriminator"] = new JsonObject { ["propertyName"] = discriminator }
    };

    private static void AddDiscriminator(Type type, JsonNode schema)
    {
        if (schema is not JsonObject value) return;
        if (type.GetCustomAttribute<JsonPolymorphicAttribute>() is { } polymorphic)
        {
            var mapping = new JsonObject();
            foreach (var derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
                if (derived.TypeDiscriminator is string tag) mapping[tag] = "#/components/schemas/" + Name(derived.DerivedType);
            value["discriminator"] = new JsonObject { ["propertyName"] = polymorphic.TypeDiscriminatorPropertyName ?? "$type", ["mapping"] = mapping };
        }
        if (value["properties"] is not JsonObject properties) return;
        string? property = null; object? discriminator = null;
        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
        {
            var match = parent.GetCustomAttributes<JsonDerivedTypeAttribute>().FirstOrDefault(item => item.DerivedType == type);
            if (match?.TypeDiscriminator is null) continue;
            property = parent.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName ?? "$type";
            discriminator = match.TypeDiscriminator; break;
        }
        if (typeof(SessionMessage).IsAssignableFrom(type))
        {
            property = "type";
            discriminator = type.Name switch
            {
                nameof(UserMessage) => "user", nameof(SyntheticMessage) => "synthetic", nameof(SystemMessage) => "system", nameof(SkillMessage) => "skill",
                nameof(ShellMessage) => "shell", nameof(AssistantMessage) => "assistant", nameof(AgentSelectedMessage) => "agent-switched",
                nameof(ModelSelectedMessage) => "model-switched", nameof(LocationSwitchedMessage) => "location-switched", _ => "compaction"
            };
            properties["id"] = String("^msg_");
        }
        if (property is not null)
        {
            properties[property] = new JsonObject { ["const"] = JsonSerializer.SerializeToNode(discriminator) };
            var required = value["required"] as JsonArray ?? new JsonArray();
            if (!required.Any(item => item?.GetValue<string>() == property)) required.Add(property);
            if (required.Parent is null) value["required"] = required;
        }
        if (type == typeof(CompactionRunningMessage)) properties["status"] = new JsonObject { ["const"] = "running" };
        if (type == typeof(CompactionCompletedMessage)) properties["status"] = new JsonObject { ["const"] = "completed" };
        if (type == typeof(CompactionFailedMessage)) properties["status"] = new JsonObject { ["const"] = "failed" };
        if (type.Name == "SessionActive" && type.Namespace == "OpenCode.Protocol.Groups") properties["type"] = new JsonObject { ["const"] = "running" };
        if (type == typeof(VcsBase)) properties["source"] = Strings("reflog", "default");
        if (type.Name == "NativeHealth" && type.Namespace == "OpenCode.Server.Documentation")
        {
            properties["healthy"] = new JsonObject { ["const"] = true };
            properties["pid"] = Number(true, 0);
            properties["state"] = Strings("starting", "ready", "failed", "stopping");
            properties["channel"] = new JsonObject { ["const"] = "dotnet" };
        }
    }

    private static string Name(Type type) => type.Namespace == "OpenCode.Schema" && Stable.TryGetValue(type.Name, out var name) ? name
        : "Native." + Regex.Replace(type.IsGenericType ? type.GetGenericTypeDefinition().FullName!.Split('`')[0] + "." + string.Join(".", type.GetGenericArguments().Select(Name)) : type.FullName!, "[^a-zA-Z0-9._-]", "_");
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static JsonObject String(string? pattern = null) => pattern is null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["pattern"] = pattern };
    private static JsonObject Strings(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject Number(bool integer, double? minimum = null, double? maximum = null)
    {
        var value = new JsonObject { ["type"] = integer ? "integer" : "number", ["x-native-finite"] = true };
        if (minimum is not null) value["minimum"] = minimum.Value;
        if (maximum is not null) value["maximum"] = maximum.Value;
        return value;
    }
    private static JsonObject Epoch() => new() { ["type"] = "integer", ["minimum"] = -62135596800000d, ["maximum"] = 253402300799999d,
        ["description"] = "Epoch milliseconds. Native decoding truncates fractional milliseconds within the DateTimeOffset range." };
    private static JsonObject Array(JsonNode items, int? minimum = null)
    { var array = new JsonObject { ["type"] = "array", ["items"] = items }; if (minimum is not null) array["minItems"] = minimum.Value; return array; }
    private static JsonObject Map(JsonNode values) => new() { ["type"] = "object", ["additionalProperties"] = values };
    private static JsonObject Object(JsonObject properties, IEnumerable<string> required) => new() { ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()), ["additionalProperties"] = false };
    private static JsonObject Any(params JsonNode[] schemas) => new() { ["anyOf"] = new JsonArray(schemas) };
    private static JsonNode NonNull(JsonNode node)
    {
        if (node is not JsonObject value) return node;
        if (value.ContainsKey("default") && value["default"] is null) value.Remove("default");
        if (value["type"] is JsonArray types)
        {
            for (var index = types.Count - 1; index >= 0; index--) if (types[index]?.GetValue<string>() == "null") types.RemoveAt(index);
            if (types.Count == 1) value["type"] = types[0]!.DeepClone();
        }
        if (value["anyOf"] is JsonArray variants)
        {
            for (var index = variants.Count - 1; index >= 0; index--) if (variants[index]?["type"]?.ToJsonString() == "\"null\"") variants.RemoveAt(index);
        }
        return value;
    }
    private static void RewriteLocalRefs(JsonNode node, string name)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var text) && text.StartsWith('#') && !text.StartsWith("#/components/", StringComparison.Ordinal))
                obj["$ref"] = "#/components/schemas/" + name + text[1..];
            foreach (var child in obj.ToArray()) if (child.Value is not null) RewriteLocalRefs(child.Value, name);
        }
        if (node is JsonArray array) foreach (var child in array) if (child is not null) RewriteLocalRefs(child, name);
    }
}
