namespace OpenCode.Core.CodeMode;

using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Adapts one captured registry to a sandbox, preserving machine output separately from model content.</summary>
internal static class CodeModeTool
{
    private const string Description = "Run JavaScript in a confined runtime to orchestrate tool calls and compose their results.\n" +
        "Imports, direct filesystem access, and timers are unavailable. Do not use fetch; all external access goes through tools.\n" +
        "Only tools listed in the captured catalog or returned by search are available. Preserve exact paths and bracket notation.\n" +
        "Prefer an explicit return; otherwise the final top-level expression is the result.\n" +
        "Await every call whose completion matters; pending calls are interrupted when execution ends. Use Promise.all for independent calls.";

    internal static ToolInfo Create(ImmutableArray<ToolInfo> registrations, CodeModeCatalog catalog, ICodeModeEvaluator evaluator,
        CodeModeLimits limits, Func<string, ToolInfo, JsonElement, ToolContext, CancellationToken, Task<ToolExecutionResult>> execute, TimeProvider clock)
    {
        var captured = registrations.GroupBy(CodeModeCatalog.QualifiedName, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return ToolInfo.FromJson("execute", Description, JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"code":{"type":"string"}},"required":["code"],"additionalProperties":false}
            """), async (input, context, ct) =>
        {
            var source = input.GetProperty("code").GetString()!;
            using var work = clock.CreateLinkedCancellationTokenSource(ct);
            work.CancelAfter(limits.TimeoutMilliseconds);
            var bindings = new Bindings(captured, catalog, limits, context, execute, work.Token);
            CodeModeEvaluation result;
            try
            {
                if (string.IsNullOrWhiteSpace(source)) throw new CodeModeDiagnosticException(new("ParseError", "Code cannot be empty."));
                if (Encoding.UTF8.GetByteCount(source) > limits.MaxSourceBytes)
                    throw new CodeModeDiagnosticException(new("InvalidDataValue", "Source exceeds the host byte limit."));
                result = await evaluator.EvaluateAsync(source, bindings, limits, work.Token);
                work.Token.ThrowIfCancellationRequested();
                if (result.Ok) result = result with { Value = CodeModeData.Copy(result.Value, limits.MaxBoundaryBytes, "Execution result") };
            }
            catch (CodeModeDiagnosticException error) { result = new(default, error.Diagnostic); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.IsCancellationRequested)
            { result = new(default, new("TimeoutExceeded", $"Execution timed out after {limits.TimeoutMilliseconds}ms.")); }
            finally
            {
                bindings.Close();
                await work.CancelAsync();
                // Never abandon leaf cleanup or let a returned program retain capabilities.
                await bindings.SettleAsync();
            }
            ct.ThrowIfCancellationRequested();
            bindings.ThrowControlFailure();
            return bindings.Result(result);
        }, JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{
              "output":{"type":"string"},
              "toolCalls":{"type":"array","items":{"type":"object","properties":{"tool":{"type":"string"},"status":{"enum":["running","completed","error"]},"input":{"type":"object"}},"required":["tool","status"]}},
              "error":{"const":true},
              "files":{"type":"array","items":{"type":"object","properties":{"data":{"type":"string"},"mime":{"type":"string"},"name":{"type":"string"}},"required":["data","mime"]}}
            },"required":["output","toolCalls","files"]}
            """), new(CodeMode: false));
    }

    private sealed record Call(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Input);
    private sealed record File(
        [property: JsonPropertyName("data")] string Data,
        [property: JsonPropertyName("mime")] string Mime,
        [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name);

    private sealed class Bindings(ImmutableDictionary<string, ToolInfo> tools, CodeModeCatalog catalog, CodeModeLimits limits,
        ToolContext context, Func<string, ToolInfo, JsonElement, ToolContext, CancellationToken, Task<ToolExecutionResult>> execute,
        CancellationToken work) : ICodeModeBindings
    {
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _progress = new(1, 1);
        private readonly List<Call> _calls = [];
        private readonly List<Task> _running = [];
        private readonly SortedDictionary<int, File[]> _files = [];
        private readonly List<string> _logs = [];
        private int _logBytes;
        private int _logCount;
        private bool _closed;
        private ExceptionDispatchInfo? _controlFailure;

        public Task<JsonElement> CallAsync(IReadOnlyList<string> path, IReadOnlyList<JsonElement> arguments)
        {
            ArgumentNullException.ThrowIfNull(path);
            var name = string.Join(".", path);
            if (!tools.TryGetValue(name, out var tool))
                throw new CodeModeDiagnosticException(new("UnknownTool", $"Unknown tool '{name}'.",
                    Suggestions: ["The tool may have been removed or renamed. Use search to find available tools."]));
            var input = Input(arguments, name);
            lock (_gate)
            {
                var index = Admit(name, input);
                var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _running.Add(RunAsync(index, tool, input, completion));
                return completion.Task;
            }
        }

        private async Task RunAsync(int index, ToolInfo tool, JsonElement input, TaskCompletionSource<JsonElement> completion)
        {
            // Register owned work before even a synchronously completing executor runs.
            await Task.Yield();
            try
            {
                work.ThrowIfCancellationRequested();
                await ProgressAsync();
                var result = await execute(tool.Id, tool, input, context, work);
                work.ThrowIfCancellationRequested();
                JsonElement value;
                try
                {
                    // Captured executor has already encoded declared output with its codec.
                    // Never substitute display content when output is explicitly JSON null.
                    value = result.HasOutput ? JsonSerializer.SerializeToElement(result.Output) : JsonSerializer.SerializeToElement(
                        string.Join("\n", (result.Content ?? []).OfType<ToolTextContent>().Select(part => part.Text)) is { Length: > 0 } text ? text : null);
                    value = CodeModeData.Copy(value, limits.MaxBoundaryBytes, $"Result from tool '{tool.Id}'");
                }
                catch (Exception error) when (error is JsonException or NotSupportedException or CodeModeDiagnosticException)
                { throw new CodeModeDiagnosticException(new("InvalidToolOutput", $"Invalid output from tool '{tool.Id}': {error.Message}")); }
                lock (_gate)
                {
                    _files[index] = (result.Content ?? []).OfType<ToolFileContent>()
                        .Where(file => file.Uri.StartsWith($"data:{file.Mime};base64,", StringComparison.Ordinal))
                        .Select(file => new File(file.Uri[("data:" + file.Mime + ";base64,").Length..], file.Mime, file.Name)).ToArray();
                    _calls[index] = _calls[index] with { Status = "completed" };
                }
                await ProgressAsync();
                completion.TrySetResult(value);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _calls[index] = _calls[index] with { Status = "error" };
                    if (error is not (ToolExecutionException or CodeModeDiagnosticException) && !(error is OperationCanceledException && work.IsCancellationRequested))
                        _controlFailure ??= ExceptionDispatchInfo.Capture(error);
                }
                try { await ProgressAsync(); }
                catch (Exception progress) { lock (_gate) _controlFailure ??= ExceptionDispatchInfo.Capture(progress); }
                completion.TrySetException(error is ToolExecutionException ? new CodeModeDiagnosticException(new("ToolFailure", error.Message)) : error);
                // Observe abandoned host tasks; guest promise observation belongs to the evaluator.
                _ = completion.Task.Exception;
            }
        }

        public JsonElement Search(IReadOnlyList<JsonElement> arguments)
        {
            var input = Input(arguments, "search");
            if (input.ValueKind != JsonValueKind.Object) throw new CodeModeDiagnosticException(new("InvalidToolInput", "Search input must be an object."));
            CodeModeSearchInput request;
            try
            {
                request = new(input.TryGetProperty("query", out var query) ? query.GetString() : null,
                    input.TryGetProperty("namespace", out var space) ? space.GetString() : null,
                    input.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 10,
                    input.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0);
                if (query.ValueKind == JsonValueKind.Null || space.ValueKind == JsonValueKind.Null || request.Limit < 1 || request.Offset < 0)
                    throw new ArgumentException("Search requires optional strings, a positive integer limit and a nonnegative integer offset.");
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
            { throw new CodeModeDiagnosticException(new("InvalidToolInput", error.Message)); }
            lock (_gate)
            {
                var index = Admit("search", input);
                try
                {
                    var result = CodeModeData.Copy(JsonSerializer.SerializeToElement(catalog.Search(request)), limits.MaxBoundaryBytes, "Search result");
                    _calls[index] = _calls[index] with { Status = "completed" };
                    return result;
                }
                catch (CodeModeDiagnosticException)
                {
                    _calls[index] = _calls[index] with { Status = "error" };
                    throw;
                }
            }
        }

        public ImmutableArray<string> Keys(IReadOnlyList<string> path)
        {
            lock (_gate) { RequireOpen(); return catalog.Keys(path); }
        }

        public void Log(string formattedLine)
        {
            ArgumentNullException.ThrowIfNull(formattedLine);
            lock (_gate)
            {
                RequireOpen();
                _logCount++;
                var bytes = Encoding.UTF8.GetByteCount(formattedLine) + 1L;
                if (_logCount != _logs.Count + 1 || _logBytes + bytes > limits.MaxOutputBytes) return;
                _logs.Add(formattedLine);
                _logBytes += (int)bytes;
            }
        }

        private JsonElement Input(IReadOnlyList<JsonElement> arguments, string name)
        {
            if (arguments.Count != 1) throw new CodeModeDiagnosticException(new("InvalidToolInput", $"Tool '{name}' expects exactly one input object."));
            try { return CodeModeData.Copy(arguments[0], limits.MaxBoundaryBytes, $"Arguments for tool '{name}'"); }
            catch (CodeModeDiagnosticException error) { throw new CodeModeDiagnosticException(new("InvalidToolInput", error.Message)); }
        }

        private int Admit(string name, JsonElement input)
        {
            RequireOpen();
            if (_calls.Count >= limits.MaxToolCalls)
                throw new CodeModeDiagnosticException(new("ToolCallLimitExceeded", $"Execution exceeded its tool-call limit of {limits.MaxToolCalls}."));
            var shown = input.ValueKind == JsonValueKind.Null || input.ValueKind == JsonValueKind.Object && !input.EnumerateObject().Any()
                ? (JsonElement?)null : input.ValueKind == JsonValueKind.Object ? input : JsonSerializer.SerializeToElement(new { input });
            _calls.Add(new(name, "running", shown));
            return _calls.Count - 1;
        }

        private void RequireOpen()
        {
            work.ThrowIfCancellationRequested();
            if (_closed) throw new OperationCanceledException("The program has ended; its tool capabilities are closed.");
        }

        private async Task ProgressAsync()
        {
            await _progress.WaitAsync();
            try
            {
                Call[] calls;
                lock (_gate) calls = _calls.ToArray();
                await context.ReportProgress(new Dictionary<string, object> { ["toolCalls"] = calls });
            }
            finally { _progress.Release(); }
        }

        internal void Close() { lock (_gate) _closed = true; }
        internal async Task SettleAsync()
        {
            Task[] running;
            lock (_gate) running = _running.ToArray();
            await Task.WhenAll(running);
            _progress.Dispose();
        }
        internal void ThrowControlFailure() => _controlFailure?.Throw();

        internal ToolExecutionResult Result(CodeModeEvaluation result)
        {
            var valueBytes = result.Ok ? Encoding.UTF8.GetByteCount(result.Value.GetRawText()) : 0;
            var output = result.Ok ? valueBytes > limits.MaxOutputBytes
                ? CodeModeData.Truncate(result.Value.GetRawText(), limits.MaxOutputBytes) + $" [result truncated: {valueBytes} bytes exceeds the {limits.MaxOutputBytes}-byte output limit; return a smaller value]"
                : result.Value.ValueKind == JsonValueKind.String ? result.Value.GetString()! : JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true })
                : string.Join("\n", new[] { result.Error!.Message }.Concat((result.Error.Suggestions ?? []).Where(hint => !result.Error.Message.Contains(hint, StringComparison.Ordinal)))).Trim();
            var warnings = new List<CodeModeDiagnostic>();
            var warningBytes = 0;
            foreach (var warning in result.Warnings.IsDefault ? [] : result.Warnings)
            {
                var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(warning)) + 1;
                if (warningBytes + bytes > limits.MaxOutputBytes) break;
                warnings.Add(warning);
                warningBytes += bytes;
            }
            if (!result.Warnings.IsDefault && warnings.Count < result.Warnings.Length)
                warnings.Add(new("Truncated", $"{result.Warnings.Length - warnings.Count} additional warnings omitted by the output limit."));
            var logs = new List<string>();
            var logBudget = Math.Max(0, limits.MaxOutputBytes - Math.Min(valueBytes, limits.MaxOutputBytes));
            foreach (var line in _logs)
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (bytes > logBudget) break;
                logBudget -= bytes;
                logs.Add(line);
            }
            if (logs.Count < _logCount) logs.Add($"[logs truncated: showing {logs.Count} of {_logCount} lines]");
            var text = string.Join("\n\n", new[] { output,
                result.Ok && warnings.Count > 0 ? "Warnings:\n" + string.Join("\n", warnings.Select(warning => $"- [{warning.Kind}] {warning.Message}")) : "",
                logs.Count > 0 ? "Logs:\n" + string.Join("\n", logs) : "" }.Where(part => part.Length > 0));
            var files = _files.Values.SelectMany(items => items).ToArray();
            var calls = _calls.ToArray();
            var machine = new Dictionary<string, object> { ["output"] = text, ["toolCalls"] = calls, ["files"] = files };
            var metadata = new Dictionary<string, object> { ["toolCalls"] = calls };
            if (!result.Ok) { machine["error"] = true; metadata["error"] = true; }
            return new ToolExecutionResult
            {
                Output = JsonSerializer.SerializeToElement(machine),
                Content = new ToolContent[] { new ToolTextContent(text) }.Concat(files.Select(file => new ToolFileContent($"data:{file.Mime};base64,{file.Data}", file.Mime, file.Name))).ToArray(),
                Metadata = metadata
            };
        }
    }
}
