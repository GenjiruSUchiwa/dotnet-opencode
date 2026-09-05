namespace OpenCode.Core.Tools;

using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Text.Json;
using OpenCode.Core.CodeMode;
using OpenCode.Schema;

public sealed record ToolInvocation(string Name, JsonElement Input);
public interface IToolExecutionHooks
{
    ValueTask<ToolInvocation> BeforeAsync(ToolInvocation invocation, ToolContext context, CancellationToken ct);
    ValueTask<ToolExecutionResult> AfterSuccessAsync(string name, JsonElement input, ToolContext context, ToolExecutionResult result, CancellationToken ct);
    ValueTask<ToolExecutionException> AfterErrorAsync(string name, JsonElement input, ToolContext context, ToolExecutionException error, CancellationToken ct);
}

/// <summary>Immutable request catalog and captured Tool.Info objects. Disposing/reloading registrations affects
/// future snapshots only; closures and codecs may still observe producer-owned mutable state.</summary>
public sealed class ToolSnapshot
{
    private readonly IReadOnlyDictionary<string, ToolInfo> _direct;
    private readonly IToolExecutionHooks? _hooks;
    private readonly ImmutableArray<ToolInfo> _registrations;
    private readonly bool _codeModeEnabled;
    private readonly ToolInfo? _execute;
    private readonly TimeProvider _clock;
    public IReadOnlyList<ToolDefinition> Definitions { get; }
    public IReadOnlyList<ToolDefinition>? CodeModeCatalog { get; }
    /// <summary>Exact qualified paths and signatures, unlike flattened provider definitions.</summary>
    public CodeModeCatalog? CodeModeDiscovery { get; }
    public bool CodeModeExecutable => _execute is not null;

    internal ToolSnapshot(IEnumerable<ToolInfo> tools, bool codeModeEnabled, IToolExecutionHooks? hooks,
        ICodeModeEvaluator? evaluator = null, CodeModeLimits? limits = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var captured = tools.ToImmutableArray();
        _registrations = captured;
        _codeModeEnabled = codeModeEnabled;
        _hooks = hooks;
        _direct = new ReadOnlyDictionary<string, ToolInfo>(captured.Where(tool => tool.Options?.CodeMode == false).ToDictionary(tool => tool.Id, StringComparer.Ordinal));
        CodeModeCatalog = codeModeEnabled ? Array.AsReadOnly(captured.Where(tool => tool.Options?.CodeMode != false).Select(Definition).ToArray()) : null;
        CodeModeDiscovery = codeModeEnabled ? new(captured.Where(tool => tool.Options?.CodeMode != false)) : null;
        _execute = codeModeEnabled && evaluator is not null
            ? CodeModeTool.Create(captured.Where(tool => tool.Options?.CodeMode != false).ToImmutableArray(), CodeModeDiscovery!, evaluator,
                limits ?? throw new ArgumentNullException(nameof(limits)), ExecuteNestedAsync, _clock) : null;
        Definitions = Array.AsReadOnly(_direct.Values.OrderBy(tool => tool.Id, StringComparer.Ordinal).Select(Definition)
            .Concat(_execute is null ? [] : new[] { Definition(_execute) }).ToArray());
    }

    /// <summary>
    /// Bind an explicitly supplied, trusted sandbox adapter to this immutable snapshot.
    /// No engine is installed implicitly. The host must bind every new request snapshot
    /// and use CodeModeDiscovery for its instruction epoch; old snapshots remain unchanged.
    /// </summary>
    public ToolSnapshot WithCodeMode(ICodeModeEvaluator evaluator, CodeModeLimits limits, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(limits);
        return new(_registrations, _codeModeEnabled, _hooks, evaluator, limits, clock ?? _clock);
    }

    public async Task<ToolExecutionResult> ExecuteAsync(string name, JsonElement input, ToolContext context,
        CancellationToken ct = default, IReadOnlyDictionary<string, ToolDefinition>? definitions = null)
    {
        ct.ThrowIfCancellationRequested();
        var invocation = new ToolInvocation(name, input);
        if (_hooks is not null) invocation = await _hooks.BeforeAsync(invocation, context, ct).ConfigureAwait(true);
        var requested = definitions?.GetValueOrDefault(invocation.Name);
        if (definitions is not null && requested is null && (_direct.ContainsKey(invocation.Name) || invocation.Name == "execute" && _execute is not null))
            throw new ToolExecutionException($"Tool is not available for this request: {invocation.Name}");
        name = requested?.Name ?? invocation.Name;
        var tool = name == "execute" && _execute is not null ? _execute : _direct.GetValueOrDefault(name);
        if (tool is null) throw new ToolExecutionException($"Unknown tool: {name}");
        ToolExecutionResult result;
        try { result = await Execute(tool, invocation.Input, context, ct).ConfigureAwait(true); }
        catch (ToolExecutionException error)
        {
            if (_hooks is not null) throw await _hooks.AfterErrorAsync(name, invocation.Input, context, error, ct).ConfigureAwait(true);
            throw;
        }
        if (_hooks is not null) result = await _hooks.AfterSuccessAsync(name, invocation.Input, context, result, ct).ConfigureAwait(true);
        return Normalize(result);
    }

    private async Task<ToolExecutionResult> ExecuteNestedAsync(string name, ToolInfo tool, JsonElement input,
        ToolContext context, CancellationToken ct)
    {
        // Upstream nested calls keep their captured registration; the before hook can
        // repair input, but cannot escape that capture by renaming the tool.
        var invocation = _hooks is null ? new ToolInvocation(name, input) : await _hooks.BeforeAsync(new(name, input), context, ct).ConfigureAwait(true);
        ToolExecutionResult result;
        try { result = await Execute(tool, invocation.Input, context, ct).ConfigureAwait(true); }
        catch (ToolExecutionException error)
        {
            if (_hooks is not null) throw await _hooks.AfterErrorAsync(name, invocation.Input, context, error, ct).ConfigureAwait(true);
            throw;
        }
        if (_hooks is not null) result = await _hooks.AfterSuccessAsync(name, invocation.Input, context, result, ct).ConfigureAwait(true);
        return Normalize(result);
    }

    private static async Task<ToolExecutionResult> Execute(ToolInfo tool, JsonElement input, ToolContext context, CancellationToken ct)
    {
        object? decoded;
        try { decoded = await tool.Input.DecodeAsync(input, ct).ConfigureAwait(true); }
        catch (ToolValidationException error)
        {
            throw new ToolExecutionException($"Invalid arguments for tool \"{tool.Id}\":\n{error.Message}\n\nUpdate the arguments and call the tool again.", error);
        }
        // .NET exceptions have no Effect typed-failure/defect distinction. Only the explicit
        // ToolExecutionException contract is recoverable; security/control flow and defects pass through.
        var result = await tool.Execute(decoded, context, ct).ConfigureAwait(true);
        if (result is null) throw new ToolContractException("Tool returned no result.");
        if (tool.Output is null)
        {
            if (result.HasOutput) throw new ToolContractException("Tool result declared output without an output schema.");
            return Normalize(result);
        }
        if (!result.HasOutput) throw new ToolExecutionException("Tool did not return its declared output.");
        try
        {
            var encoded = await tool.Output.EncodeAsync(result.Output, ct).ConfigureAwait(true);
            if (encoded.ValueKind == JsonValueKind.Undefined)
                throw new ToolValidationException([new("root", "Output codec returned a non-JSON value.")]);
            result = result with { Output = encoded.Clone() };
        }
        catch (ToolValidationException error)
        { throw new ToolExecutionException($"Tool returned an invalid value for its output schema: {error.Message}", error); }
        return Normalize(result);
    }

    private static ToolExecutionResult Normalize(ToolExecutionResult result)
    {
        var content = result.Content is { Count: > 0 } ? result.Content.ToArray() :
            new ToolContent[] { new ToolTextContent(result.HasOutput ? Stringify(result.Output) : "undefined") };
        if (content.Any(item => item is not (ToolTextContent or ToolFileContent))) throw new ToolContractException("Tool content must contain text or file parts.");
        return result with { Content = Array.AsReadOnly(content) };
    }

    private static string Stringify(object? output) => output switch
    {
        string value => value,
        JsonElement { ValueKind: JsonValueKind.String } value => value.GetString()!,
        JsonElement value => value.GetRawText(),
        _ => JsonSerializer.Serialize(output)
    };

    private static ToolDefinition Definition(ToolInfo tool) => new(tool.Id, tool.Description, tool.Input.JsonSchema.Clone(), tool.Output?.JsonSchema.Clone());
}
