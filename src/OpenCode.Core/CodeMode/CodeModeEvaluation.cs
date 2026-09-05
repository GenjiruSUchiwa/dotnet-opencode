namespace OpenCode.Core.CodeMode;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record CodeModeDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodeModeSourceLocation? Location = null,
    [property: JsonPropertyName("suggestions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Suggestions = null);
public sealed record CodeModeSourceLocation([property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("column")] int Column);

/// <summary>Only these expected program/boundary failures may become guest-catchable errors.</summary>
public sealed class CodeModeDiagnosticException(CodeModeDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public CodeModeDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Interpreter resource control, never a guest-catchable error/rejection.</summary>
internal sealed class CodeModeLimitException(CodeModeDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    internal CodeModeDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Host-selected finite budgets; these are not upstream's unlimited defaults.</summary>
public sealed record CodeModeLimits
{
    public int TimeoutMilliseconds { get; }
    public int MaxToolCalls { get; }
    public int MaxOutputBytes { get; }
    /// <summary>Cumulative owner-thread allocation detection; not a retained-heap or process quota.</summary>
    public long MaxAllocatedBytes { get; }
    public int MaxRecursionDepth { get; }
    /// <summary>Interpreter checkpoint count; includes built-in work checks, not only statements.</summary>
    public int MaxExecutionChecks { get; }
    public int MaxSyntaxNodes { get; }
    public int MaxSourceBytes { get; }
    public int MaxBoundaryBytes { get; }

    public CodeModeLimits(int timeoutMilliseconds, int maxToolCalls, int maxOutputBytes, long maxAllocatedBytes,
        int maxRecursionDepth, int maxSourceBytes = 262144, int maxBoundaryBytes = 4194304,
        int maxExecutionChecks = 100000, int maxSyntaxNodes = 20000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxToolCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAllocatedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecursionDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExecutionChecks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSyntaxNodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBoundaryBytes, 1);
        TimeoutMilliseconds = timeoutMilliseconds;
        MaxToolCalls = maxToolCalls;
        MaxOutputBytes = maxOutputBytes;
        MaxAllocatedBytes = maxAllocatedBytes;
        MaxRecursionDepth = maxRecursionDepth;
        MaxExecutionChecks = maxExecutionChecks;
        MaxSyntaxNodes = maxSyntaxNodes;
        MaxSourceBytes = maxSourceBytes;
        MaxBoundaryBytes = maxBoundaryBytes;
    }
}

public sealed record CodeModeEvaluation(JsonElement Value, CodeModeDiagnostic? Error = null,
    ImmutableArray<CodeModeDiagnostic> Warnings = default)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The entire guest capability surface. The adapter exposes guest functions, never
/// this CLR object, delegates, reflection, ToolInfo, or the host's invocation context.
/// Paths are inert segments (including constructor/prototype); data keys are not.
/// </summary>
public interface ICodeModeBindings
{
    Task<JsonElement> CallAsync(IReadOnlyList<string> path, IReadOnlyList<JsonElement> arguments);
    JsonElement Search(IReadOnlyList<JsonElement> arguments);
    ImmutableArray<string> Keys(IReadOnlyList<string> path);
    void Log(string formattedLine);
}

/// <summary>
/// Trusted in-process capability adapter, not an OS security boundary.
/// An implementation creates a fresh confined realm per call, checks allocation,
/// recursion, source and cancellation budgets in its interpreter (including busy loops),
/// provides no imports, timers, fetch, filesystem, process or CLR access, implements
/// top-level await/return and the final-expression result, and supervises all guest
/// promises. It reports unobserved rejections and cancels pending guest work on exit.
/// Only CodeModeDiagnosticException is guest-catchable; host control failures and
/// cancellation must escape evaluation. Returning must mean engine work has stopped.
/// Task.WaitAsync/Task.Run around unrestricted eval does not meet this contract.
/// Constraints are cooperative: parsing, single allocations and host operations
/// can overshoot a budget. Allocation accounting is not a hard memory quota.
/// </summary>
public interface ICodeModeEvaluator
{
    Task<CodeModeEvaluation> EvaluateAsync(string source, ICodeModeBindings bindings, CodeModeLimits limits, CancellationToken ct);
}

internal static class CodeModeData
{
    internal static JsonElement Copy(JsonElement value, int maxBytes, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new CodeModeDiagnosticException(new("InvalidDataValue", label + " must be JSON data."));
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maxBytes)
            throw new CodeModeDiagnosticException(new("InvalidDataValue", label + " exceeds the host boundary byte limit."));
        Validate(value, label, 0);
        return value.Clone();
    }

    private static void Validate(JsonElement value, string label, int depth)
    {
        if (depth > 32) throw new CodeModeDiagnosticException(new("InvalidDataValue", label + " exceeds the maximum value depth of 32."));
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "__proto__" or "constructor" or "prototype")
                    throw new CodeModeDiagnosticException(new("InvalidDataValue", label + " contains blocked property '" + property.Name + "'."));
                Validate(property.Value, label, depth + 1);
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Validate(item, label, depth + 1);
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new CodeModeDiagnosticException(new("InvalidDataValue", label + " contains a non-finite JSON number."));
    }

    internal static string Truncate(string value, int maxBytes)
    {
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes) break;
            result.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
