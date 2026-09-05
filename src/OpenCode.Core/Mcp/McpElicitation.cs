namespace OpenCode.Core.Mcp;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenCode.Schema;

/// <summary>
/// Adapter to the existing Location-owned Form service. It owns pending forms, validation, events,
/// and terminal state; MCP owns only protocol conversion and URL-completion correlation.
/// </summary>
public interface IMcpElicitationForms
{
    /// <summary>
    /// Create the supplied form and wait for an answered/cancelled state. Cancellation must cancel
    /// and remove the pending form before this task completes. Never return a pending state.
    /// </summary>
    Task<FormState> AskAsync(FormInfo form, CancellationToken ct);

    /// <summary>
    /// Validate and apply an answer. Return false only when absent or already settled; other errors
    /// propagate. Used to acknowledge a pending external field when its MCP server signals completion.
    /// </summary>
    Task<bool> TryReplyAsync(FormId id, FormAnswer answer, CancellationToken ct);
}

/// <summary>One transport's callbacks. No persisted Session or independent Form registry is created.</summary>
internal sealed class McpElicitationConnection : IAsyncDisposable
{
    private const string GlobalOwner = "global";
    private const string ExternalKey = "elicitation";
    private readonly IMcpElicitationForms _forms;
    private readonly string _server;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _cancellation;
    private readonly ConcurrentDictionary<string, FormId> _urls = new(StringComparer.Ordinal);

    public McpElicitationConnection(IMcpElicitationForms forms, string server, CancellationToken shutdown)
    {
        _forms = forms;
        _server = server;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _cancellation = _lifetime.Token;
    }

    public async ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams? request, CancellationToken ct)
    {
        if (request is null) throw new McpProtocolException("Missing MCP elicitation parameters", McpErrorCode.InvalidParams);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation);
        linked.Token.ThrowIfCancellationRequested();
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["kind"] = JsonSerializer.SerializeToElement("mcp-elicitation", OpenCodeJsonContext.Default.String),
            ["server"] = JsonSerializer.SerializeToElement(_server, OpenCodeJsonContext.Default.String),
            ["message"] = JsonSerializer.SerializeToElement(request.Message
                ?? throw new McpProtocolException("MCP elicitation requires a message", McpErrorCode.InvalidParams), OpenCodeJsonContext.Default.String)
        };
        if (request.Mode == "url")
        {
            var id = request.ElicitationId ?? throw new McpProtocolException("URL elicitation requires an ID", McpErrorCode.InvalidParams);
            var url = request.Url ?? throw new McpProtocolException("URL elicitation requires a URL", McpErrorCode.InvalidParams);
            metadata["elicitationID"] = JsonSerializer.SerializeToElement(id, OpenCodeJsonContext.Default.String);
            var form = new FormInfo(FormId.Create(), GlobalOwner, $"{_server} is requesting input",
                [new FormExternalField { Key = ExternalKey, Url = url }], metadata);
            _urls[id] = form.Id;
            try
            {
                var state = await _forms.AskAsync(form, linked.Token).ConfigureAwait(true);
                linked.Token.ThrowIfCancellationRequested();
                return TerminalResult(state, includeContent: false);
            }
            finally { _urls.TryRemove(new KeyValuePair<string, FormId>(id, form.Id)); }
        }
        if (request.Mode is not (null or "form"))
            throw new McpProtocolException("Unsupported MCP elicitation mode", McpErrorCode.InvalidParams);
        var schema = request.RequestedSchema ?? throw new McpProtocolException("Form elicitation requires a schema", McpErrorCode.InvalidParams);
        var fields = (schema.Properties ?? throw new McpProtocolException("MCP form schema requires properties", McpErrorCode.InvalidParams))
            .Select(item => ToField(item.Key, item.Value, schema.Required?.Contains(item.Key) == true)).ToArray();
        // Source explicitly accepts a valid zero-field schema. Do not turn malformed schemas or a
        // missing host adapter into empty approved forms. FormInfo itself requires at least one field.
        if (fields.Length == 0) return new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement>() };
        var answer = await _forms.AskAsync(new FormInfo(FormId.Create(), GlobalOwner, $"{_server} is requesting input", fields, metadata), linked.Token).ConfigureAwait(true);
        linked.Token.ThrowIfCancellationRequested();
        return TerminalResult(answer, includeContent: true);
    }

    public async ValueTask CompleteAsync(JsonRpcNotification notification, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation);
        linked.Token.ThrowIfCancellationRequested();
        var completed = notification.Params?.Deserialize<ElicitationCompleteNotificationParams>(McpJsonUtilities.DefaultOptions)
            ?? throw new McpProtocolException("Missing MCP elicitation completion parameters", McpErrorCode.InvalidParams);
        if (completed.ElicitationId is null)
            throw new McpProtocolException("MCP elicitation completion requires an ID", McpErrorCode.InvalidParams);
        if (!_urls.TryGetValue(completed.ElicitationId, out var form)) return;
        await _forms.TryReplyAsync(form, new FormAnswer(new Dictionary<string, FormValue>
        {
            [ExternalKey] = new FormValue.Boolean(true)
        }), linked.Token).ConfigureAwait(true);
    }

    private static ElicitResult TerminalResult(FormState state, bool includeContent) => state switch
    {
        FormCancelledState => new() { Action = "cancel" },
        FormAnsweredState answered => new()
        {
            Action = "accept",
            Content = includeContent ? answered.Answer.ToDictionary(item => item.Key,
                item => JsonSerializer.SerializeToElement(item.Value, OpenCodeJsonContext.Default.FormValue), StringComparer.Ordinal) : null
        },
        _ => throw new InvalidOperationException("The MCP Form adapter must return an answered or cancelled state.")
    };

    private static FormField ToField(string key, ElicitRequestParams.PrimitiveSchemaDefinition property, bool required)
    {
        var title = property.Title is { Length: > 0 } value && !Regex.IsMatch(value.Trim(), @"^(?:boolean|string|number|integer|array|object)(?:\s+with\b.*|\s+in\b.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)
            ? value : property.Description ?? key;
        FormInputField field = property switch
        {
            ElicitRequestParams.BooleanSchema boolean => new FormBooleanField { Key = key, Default = boolean.Default },
            ElicitRequestParams.NumberSchema { Type: "integer" } integer => new FormIntegerField
            { Key = key, Minimum = integer.Minimum, Maximum = integer.Maximum, Default = integer.Default },
            ElicitRequestParams.NumberSchema { Type: "number" } number => new FormNumberField
            { Key = key, Minimum = number.Minimum, Maximum = number.Maximum, Default = number.Default },
            ElicitRequestParams.StringSchema text => new FormStringField
            { Key = key, Format = text.Format, MinLength = text.MinLength, MaxLength = text.MaxLength, Default = text.Default },
            ElicitRequestParams.UntitledSingleSelectEnumSchema select => new FormStringField
            { Key = key, Options = select.Enum.Select(value => new FormOption(value, value)).ToArray(), Custom = false, Default = select.Default },
            ElicitRequestParams.TitledSingleSelectEnumSchema select => new FormStringField
            { Key = key, Options = select.OneOf.Select(option => new FormOption(option.Const, option.Title)).ToArray(), Custom = false, Default = select.Default },
#pragma warning disable MCP9001 // The TS source also supports legacy enumNames sent by existing servers.
            ElicitRequestParams.LegacyTitledEnumSchema select => new FormStringField
            {
                Key = key, Options = select.Enum.Select((value, index) => new FormOption(value,
                    select.EnumNames is { } names && index < names.Count ? names[index] ?? value : value)).ToArray(),
                Custom = false, Default = select.Default
            },
#pragma warning restore MCP9001
            ElicitRequestParams.UntitledMultiSelectEnumSchema select => new FormMultiselectField
            {
                Key = key, Options = select.Items.Enum.Select(value => new FormOption(value, value)).ToArray(),
                Custom = false, MinItems = select.MinItems, MaxItems = select.MaxItems, Default = select.Default?.ToArray()
            },
            ElicitRequestParams.TitledMultiSelectEnumSchema select => new FormMultiselectField
            {
                Key = key, Options = select.Items.AnyOf.Select(option => new FormOption(option.Const, option.Title)).ToArray(),
                Custom = false, MinItems = select.MinItems, MaxItems = select.MaxItems, Default = select.Default?.ToArray()
            },
            _ => throw new McpProtocolException("Unsupported MCP elicitation field schema", McpErrorCode.InvalidParams)
        };
        return field with { Title = title, Description = property.Description == title ? null : property.Description, Required = required ? true : null };
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(true);
        _urls.Clear();
        _lifetime.Dispose();
    }
}
