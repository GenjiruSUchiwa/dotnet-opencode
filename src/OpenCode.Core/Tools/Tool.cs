namespace OpenCode.Core.Tools;

using System.Text.Json;
using OpenCode.Schema;

public sealed record ToolExecutionResult
{
    public IReadOnlyList<ToolContent>? Content { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    public bool HasOutput { get; private init; }
    public object? Output { get; init { field = value; HasOutput = true; } }

    public ToolExecutionResult() { }
    public ToolExecutionResult(string content) => Content = [new ToolTextContent(content)];
    public ToolExecutionResult(string content, object? output, IReadOnlyDictionary<string, object>? metadata = null) : this(content)
    {
        Output = output;
        Metadata = metadata;
    }
}

public sealed record ToolOptions(string? Namespace = null, string? Permission = null, bool? CodeMode = null, bool? Pinned = null);

/// <summary>The single schema-erased executable registration, analogous to Tool.Info. Producer codecs may
/// decode JSON to native values. The executor and codecs retain producer-owned references across snapshots.</summary>
public sealed record ToolInfo(
    string Name,
    string Description,
    IToolValueCodec Input,
    Func<object?, ToolContext, CancellationToken, Task<ToolExecutionResult>> Execute,
    IToolValueCodec? Output = null,
    ToolOptions? Options = null)
{
    public string Id => EffectiveName(Name, Options?.Namespace);

    public static ToolInfo FromJson(string name, string description, JsonElement input,
        Func<JsonElement, ToolContext, CancellationToken, Task<ToolExecutionResult>> execute,
        JsonElement? output = null, ToolOptions? options = null) =>
        new(name, description, new JsonToolCodec(input), (value, context, ct) => execute((JsonElement)value!, context, ct),
            output is { } schema ? new JsonToolCodec(schema) : null, options);

    public static string NormalizedName(string name) => string.Concat(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_'));

    public static string EffectiveName(string name, string? space = null) =>
        space is null ? NormalizedName(name) : space.Replace('.', '_') + "_" + NormalizedName(name);
}

/// <summary>Definition data only; never contains an alternative executable.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema, JsonElement? OutputSchema = null);
public sealed record ToolRegistrationError(string Name, string Message);
/// <summary>Explicit declared model-visible failure, analogous to Tool.Error. Do not use for permission control
/// flow or programming defects. Undeclared executor exceptions propagate unchanged.</summary>
public sealed class ToolExecutionException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ToolContractException(string message) : InvalidOperationException(message);
public sealed class ToolCatalogUnavailableException(Exception failure)
    : InvalidOperationException("Tool catalog rebuild failed; complete a successful rebuild before capturing a new snapshot.", failure);

/// <summary>Codecs are immutable schema contracts. Decode/encode may reference mutable producer services,
/// but their advertised JSON schemas must not change after construction.</summary>
public interface IToolValueCodec
{
    JsonElement JsonSchema { get; }
    ValueTask<object?> DecodeAsync(JsonElement value, CancellationToken ct);
    ValueTask<JsonElement> EncodeAsync(object? value, CancellationToken ct);
}

public sealed record ToolValidationIssue(string Path, string Message);
public sealed class ToolValidationException(IReadOnlyList<ToolValidationIssue> issues) : Exception(
    string.Join("\n", issues.Take(5).Select(issue => $"- {issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ToolValidationIssue> Issues { get; } = issues;
}
